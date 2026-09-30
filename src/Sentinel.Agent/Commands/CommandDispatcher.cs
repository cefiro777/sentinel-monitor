using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Sentinel.Agent.Infrastructure;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Commands;

public interface ICommandHandler
{
    /// <summary>Тип команды или маска ("service.*").</summary>
    string Type { get; }
    Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct);
}

/// <summary>
/// Проверяет подпись, срок и белый список, защищает от повторов, выполняет обработчик.
/// Любая проверка не прошла — команда не выполняется и это фиксируется в логе.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly AgentIdentity _identity;
    private readonly IReadOnlyList<ICommandHandler> _handlers;
    private readonly ILogger<CommandDispatcher> _log;
    private readonly Ed25519PublicKeyParameters _serverKey;
    private readonly HashSet<Guid> _seen = new HashSet<Guid>();
    private readonly Queue<Guid> _seenOrder = new Queue<Guid>();

    public CommandDispatcher(AgentIdentity identity, IEnumerable<ICommandHandler> handlers, ILogger<CommandDispatcher> log)
    {
        _identity = identity;
        _handlers = new List<ICommandHandler>(handlers);
        _log = log;
        _serverKey = new Ed25519PublicKeyParameters(Convert.FromBase64String(identity.ServerPublicKey), 0);
    }

    public async Task<CommandResult> DispatchAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        var result = new CommandResult { CommandId = cmd.Id, StartedAt = DateTimeOffset.UtcNow };
        try
        {
            Validate(cmd);
            var handler = FindHandler(cmd.Type) ?? throw new InvalidOperationException($"Нет обработчика для команды {cmd.Type}");
            _log.LogInformation("Выполняется команда {Type} {Id}", cmd.Type, cmd.Id);
            result.Output = await handler.ExecuteAsync(cmd, ct);
            result.Success = true;
            _log.LogInformation("Команда {Type} {Id} выполнена", cmd.Type, cmd.Id);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _log.LogWarning("Команда {Type} {Id} отклонена/не выполнена: {Error}", cmd.Type, cmd.Id, ex.Message);
        }
        result.FinishedAt = DateTimeOffset.UtcNow;
        return result;
    }

    private void Validate(CommandEnvelope cmd)
    {
        if (cmd.AgentId != _identity.AgentId) throw new UnauthorizedAccessException("Команда адресована другому агенту.");

        var data = Encoding.UTF8.GetBytes(cmd.CanonicalString());
        var verifier = new Ed25519Signer();
        verifier.Init(false, _serverKey);
        verifier.BlockUpdate(data, 0, data.Length);
        if (!verifier.VerifySignature(Convert.FromBase64String(cmd.Signature ?? "")))
            throw new UnauthorizedAccessException("Неверная подпись команды.");

        var now = DateTimeOffset.UtcNow;
        if (cmd.ExpiresAt < now - TimeSpan.FromMinutes(1)) throw new InvalidOperationException("Срок действия команды истёк.");
        if (cmd.IssuedAt > now + TimeSpan.FromMinutes(5)) throw new InvalidOperationException("Команда из будущего — расхождение часов.");

        if (!_identity.IsCommandAllowed(cmd.Type))
            throw new UnauthorizedAccessException(
                $"Команда {cmd.Type} запрещена локальной политикой агента. Разрешить можно только на самом сервере (от администратора): " +
                $"SentinelAgent.exe allow add {cmd.Type}");

        lock (_seen)
        {
            if (_seen.Contains(cmd.Id)) throw new InvalidOperationException("Повторная команда.");
            _seen.Add(cmd.Id);
            _seenOrder.Enqueue(cmd.Id);
            while (_seenOrder.Count > 1000) _seen.Remove(_seenOrder.Dequeue());
        }
    }

    private ICommandHandler? FindHandler(string type)
    {
        foreach (var h in _handlers)
        {
            if (string.Equals(h.Type, type, StringComparison.OrdinalIgnoreCase)) return h;
            if (h.Type.EndsWith("*") && type.StartsWith(h.Type.Substring(0, h.Type.Length - 1), StringComparison.OrdinalIgnoreCase)) return h;
        }
        return null;
    }
}

// ---------------- Обработчики ----------------

public sealed class PingHandler : ICommandHandler
{
    public string Type => CommandTypes.Ping;
    public Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
        => Task.FromResult($"pong from {Environment.MachineName}, agent v{AgentPaths.Version}, {DateTimeOffset.UtcNow:O}");
}

/// <summary>config.refresh — сигнал перечитать конфиг; сам запрос делает AgentWorker.</summary>
public sealed class ConfigRefreshHandler : ICommandHandler
{
    private readonly Func<CancellationToken, Task<int>> _refresh;
    public ConfigRefreshHandler(Func<CancellationToken, Task<int>> refresh) => _refresh = refresh;
    public string Type => CommandTypes.ConfigRefresh;
    public async Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct) => "config v" + await _refresh(ct);
}

/// <summary>service.start / service.stop / service.restart. Payload: { "name": "MSSQLSERVER", "timeoutSeconds": 60 }</summary>
public sealed class ServiceControlHandler : ICommandHandler
{
    public string Type => "service.*";

    public async Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        var name = cmd.Payload?.TryGetProperty("name", out var n) == true ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Не указано имя службы (payload.name).");
        var timeout = TimeSpan.FromSeconds(cmd.Payload?.TryGetProperty("timeoutSeconds", out var t) == true ? t.GetInt32() : 60);

        using (var sc = new ServiceController(name))
        {
            var before = sc.Status;
            switch (cmd.Type.ToLowerInvariant())
            {
                case CommandTypes.ServiceStart:
                    if (sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, timeout); }
                    break;
                case CommandTypes.ServiceStop:
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout); }
                    break;
                case CommandTypes.ServiceRestart:
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout); }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
                    break;
                default:
                    throw new InvalidOperationException("Неизвестная операция над службой: " + cmd.Type);
            }
            await Task.Delay(500, ct);
            sc.Refresh();
            return $"{sc.DisplayName} ({name}): {before} → {sc.Status}";
        }
    }
}

/// <summary>reboot / shutdown. Payload: { "delaySeconds": 30, "reason": "..." }. По умолчанию запрещена политикой — разрешается при установке.</summary>
public sealed class PowerHandler : ICommandHandler
{
    private readonly string _mode;
    public PowerHandler(string mode) => _mode = mode; // reboot и shutdown — два экземпляра
    public string Type => _mode;

    public Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        var delay = cmd.Payload?.TryGetProperty("delaySeconds", out var d) == true ? Math.Max(0, d.GetInt32()) : 30;
        var reason = cmd.Payload?.TryGetProperty("reason", out var r) == true ? r.GetString() : null;
        var comment = "Sentinel: " + (string.IsNullOrWhiteSpace(reason) ? "по команде инженера" : reason);
        var args = $"{(_mode == CommandTypes.Reboot ? "/r" : "/s")} /t {delay} /f /d p:4:1 /c \"{comment.Replace("\"", "'")}\"";
        var p = Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(10_000);
        if (p is not null && p.ExitCode != 0) throw new InvalidOperationException("shutdown.exe завершился с кодом " + p.ExitCode);
        return Task.FromResult($"{_mode} запланирован через {delay} с");
    }
}

/// <summary>backup.run — внеочередной запуск задания. Payload: { "checkId": "..." }</summary>
public sealed class BackupRunHandler : ICommandHandler
{
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<CheckResult>?>> _runNow;
    public BackupRunHandler(Func<Guid, CancellationToken, Task<IReadOnlyList<CheckResult>?>> runNow) => _runNow = runNow;
    public string Type => CommandTypes.BackupRun;

    public async Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        if (cmd.Payload?.TryGetProperty("checkId", out var idEl) != true || !Guid.TryParse(idEl.GetString(), out var checkId))
            throw new ArgumentException("Нужен payload.checkId.");
        var results = await _runNow(checkId, ct) ?? throw new InvalidOperationException("Задание не найдено в конфигурации агента или модуль не поддерживается.");
        return string.Join("\n", results.Select(r => $"[{r.Status}] {r.Key} {r.Summary}"));
    }
}
