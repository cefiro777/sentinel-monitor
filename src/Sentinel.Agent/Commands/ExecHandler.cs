using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Commands;

/// <summary>
/// exec — выполнение PowerShell (или cmd) скрипта. Payload: { "script": "...", "shell": "powershell"|"cmd", "timeoutSeconds": 300 }.
/// Разрешается только явно (--allow exec): это полный доступ к серверу от LocalSystem.
/// </summary>
public sealed class ExecHandler : ICommandHandler
{
    private const int MaxOutputChars = 64 * 1024;
    private readonly ILogger<ExecHandler> _log;

    public ExecHandler(ILogger<ExecHandler> log) => _log = log;

    public string Type => CommandTypes.Exec;

    public async Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        var script = cmd.Payload?.TryGetProperty("script", out var s) == true ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("Пустой скрипт (payload.script).");
        var shell = cmd.Payload?.TryGetProperty("shell", out var sh) == true ? sh.GetString() ?? "powershell" : "powershell";
        var timeout = TimeSpan.FromSeconds(cmd.Payload?.TryGetProperty("timeoutSeconds", out var t) == true ? Math.Max(5, t.GetInt32()) : 300);

        ProcessStartInfo psi;
        if (shell.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("cmd.exe", "/d /q /c \"" + script!.Replace("\"", "\"\"") + "\"");
        }
        else
        {
            // EncodedCommand: без проблем с кавычками и кодировкой. Поток ошибок сливаем в stdout текстом —
            // иначе powershell.exe при перенаправленном stderr выдаёт CLIXML вместо читаемых сообщений.
            var wrapped = string.Join(Environment.NewLine, new[]
            {
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8",
                "$ErrorActionPreference='Continue'",
                "$ProgressPreference='SilentlyContinue'",
                "$__code=0",
                "try {",
                "& {",
                script,
                "} 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { 'ОШИБКА: ' + $_.ToString() } else { $_ } } | Out-String -Width 200 -Stream",
                "if ($LASTEXITCODE) { $__code = $LASTEXITCODE }",
                "} catch { 'ОШИБКА: ' + $_.ToString(); $__code = 1 }",
                "exit $__code",
            });
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        _log.LogWarning("exec ({Shell}) команда {Id}: {Script}", shell, cmd.Id, Truncate(script!, 300));

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using (var p = new Process { StartInfo = psi })
        {
            p.OutputDataReceived += (_, e) => { if (e.Data is not null && stdout.Length < MaxOutputChars) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < MaxOutputChars) stderr.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var exited = await Task.Run(() => p.WaitForExit((int)timeout.TotalMilliseconds), ct);
            if (!exited)
            {
                try { p.Kill(); } catch { }
                throw new TimeoutException($"Скрипт не завершился за {timeout.TotalSeconds:0} с и был остановлен. Вывод:\n{Truncate(stdout.ToString(), 4000)}");
            }
            p.WaitForExit(); // дочитать потоки

            var result = new StringBuilder();
            result.Append("exit code: ").Append(p.ExitCode).AppendLine();
            if (stdout.Length > 0) result.AppendLine(Truncate(stdout.ToString().TrimEnd(), MaxOutputChars));
            // Ошибки PowerShell уже слиты в stdout; остаточный CLIXML (прогресс-записи) в stderr бесполезен.
            var err = stderr.ToString().TrimEnd();
            if (err.Length > 0 && !err.StartsWith("#< CLIXML")) result.AppendLine("--- stderr ---").AppendLine(Truncate(err, MaxOutputChars));
            if (p.ExitCode != 0) throw new InvalidOperationException(result.ToString());
            return result.ToString();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "\n…(обрезано)";
}
