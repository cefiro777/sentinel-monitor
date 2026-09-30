using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Sentinel.Agent.Infrastructure;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Services;

/// <summary>
/// Самообновление: манифест с сервера → сравнение версий → загрузка zip → sha256 и Ed25519-подпись ключом сервера →
/// распаковка рядом → отдельный cmd-скрипт останавливает службу, подменяет файлы и запускает её обратно.
/// </summary>
public sealed class AgentUpdater
{
    private readonly AgentIdentity _identity;
    private readonly bool _runningAsService;
    private readonly ILogger<AgentUpdater> _log;

    public AgentUpdater(AgentIdentity identity, bool runningAsService, ILogger<AgentUpdater> log)
    {
        _identity = identity;
        _runningAsService = runningAsService;
        _log = log;
    }

    /// <summary>Раз в сутки проверяет обновление; при наличии — устанавливает.</summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAndInstallAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning("Проверка обновления: {Error}", ex.Message); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task<string> CheckAndInstallAsync(CancellationToken ct)
    {
        ServerClient.ConfigureTls();
        using (var http = new HttpClient(ServerClient.CreateHandler(_identity, _log)) { BaseAddress = new Uri(_identity.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) })
        {
            var manifestJson = await http.GetStringAsync("agent/manifest.json");
            var manifest = SentinelJson.Deserialize<AgentPackageManifest>(manifestJson) ?? throw new InvalidOperationException("пустой манифест");
            var current = Version.Parse(AgentPaths.Version);
            var available = Version.Parse(manifest.Version);
            if (available <= current) return $"Обновление не требуется: установлена {current}, на сервере {available}";

            _log.LogInformation("Доступно обновление агента {Available} (установлена {Current})", available, current);
            var updateDir = Path.Combine(AgentPaths.Root, "update");
            if (Directory.Exists(updateDir)) Directory.Delete(updateDir, true);
            Directory.CreateDirectory(updateDir);
            var zipPath = Path.Combine(updateDir, "SentinelAgent.zip");

            using (var resp = await http.GetAsync("agent/" + manifest.File, HttpCompletionOption.ResponseHeadersRead, ct))
            using (var fs = File.Create(zipPath))
            {
                resp.EnsureSuccessStatusCode();
                await resp.Content.CopyToAsync(fs);
            }

            // Хеш и подпись: без них файл с сервера (или из канала) не считается доверенным.
            string sha256;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zipPath)) sha256 = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(sha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("sha256 пакета не совпадает с манифестом");
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(_identity.ServerPublicKey), 0));
            var data = Encoding.ASCII.GetBytes(sha256);
            verifier.BlockUpdate(data, 0, data.Length);
            if (!verifier.VerifySignature(Convert.FromBase64String(manifest.Signature))) throw new InvalidOperationException("подпись пакета неверна — обновление отклонено");

            var extracted = Path.Combine(updateDir, "files");
            ZipFile.ExtractToDirectory(zipPath, extracted);
            if (!File.Exists(Path.Combine(extracted, "SentinelAgent.exe"))) throw new InvalidOperationException("в пакете нет SentinelAgent.exe");
            _log.LogInformation("Пакет {Version} проверен (sha256 и подпись совпали)", available);

            if (!_runningAsService)
                return $"Обновление {available} загружено и проверено в {extracted}, но агент запущен не как служба — установите вручную.";

            var exeDir = Path.GetDirectoryName(AgentPaths.ExePath)!;
            var script = Path.Combine(updateDir, "apply.cmd");
            File.WriteAllText(script, string.Join("\r\n", new[]
            {
                "@echo off",
                $"echo Sentinel Agent update {current} -> {available} > \"{updateDir}\\apply.log\"",
                $"sc stop {AgentPaths.ServiceName} >> \"{updateDir}\\apply.log\" 2>&1",
                ":wait",
                "timeout /t 2 /nobreak > nul",
                $"sc query {AgentPaths.ServiceName} | find \"STOPPED\" > nul || goto wait",
                $"xcopy /y /e /q \"{extracted}\\*\" \"{exeDir}\\\" >> \"{updateDir}\\apply.log\" 2>&1",
                $"sc start {AgentPaths.ServiceName} >> \"{updateDir}\\apply.log\" 2>&1",
                "exit /b 0",
            }), Encoding.ASCII);

            _log.LogWarning("Запускаю установку обновления {Version}: служба будет перезапущена", available);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = updateDir });
            return $"Обновление {available} устанавливается, служба перезапустится";
        }
    }
}
