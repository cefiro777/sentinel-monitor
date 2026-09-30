using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>
/// Хранилище дистрибутива агента: zip + манифест с sha256 и Ed25519-подписью ключом сервера.
/// Агент обновляется только на пакет с валидной подписью — подмена файла на сервере/в канале не пройдёт.
/// </summary>
public sealed class AgentPackageService
{
    private readonly string _dir;
    private readonly Security.KeyStore _keys;
    private readonly ILogger<AgentPackageService> _log;
    private readonly object _lock = new();

    public const string ZipName = "SentinelAgent.zip";

    public AgentPackageService(IOptions<SentinelOptions> options, Security.KeyStore keys, ILogger<AgentPackageService> log)
    {
        _dir = Path.GetFullPath(Path.Combine(options.Value.DataDir, "agent")); // Results.File относительный путь ищет в wwwroot
        Directory.CreateDirectory(_dir);
        _keys = keys;
        _log = log;
    }

    public string ZipPath => Path.Combine(_dir, ZipName);
    private string ManifestPath => Path.Combine(_dir, "manifest.json");

    public AgentPackageManifest? GetManifest()
    {
        lock (_lock)
        {
            if (!File.Exists(ManifestPath) || !File.Exists(ZipPath)) return null;
            try { return JsonSerializer.Deserialize<AgentPackageManifest>(File.ReadAllText(ManifestPath), Contracts.Json.SentinelJson.Options); }
            catch { return null; }
        }
    }

    /// <summary>Принимает загруженный zip: читает version.txt, считает sha256, подписывает, публикует.</summary>
    public async Task<AgentPackageManifest> PublishAsync(Stream zip, CancellationToken ct)
    {
        var tmp = ZipPath + ".upload";
        using (var fs = File.Create(tmp)) await zip.CopyToAsync(fs, ct);

        string version;
        try
        {
            using var archive = ZipFile.OpenRead(tmp);
            var entry = archive.GetEntry("version.txt") ?? throw new InvalidOperationException("В пакете нет version.txt — собирайте его скриптом build-agent.ps1.");
            using var reader = new StreamReader(entry.Open());
            version = (await reader.ReadToEndAsync(ct)).Trim();
            if (!Version.TryParse(version, out _)) throw new InvalidOperationException($"Некорректная версия в пакете: {version}");
            if (archive.GetEntry("SentinelAgent.exe") is null) throw new InvalidOperationException("В пакете нет SentinelAgent.exe.");
        }
        catch { File.Delete(tmp); throw; }

        string sha256;
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(tmp)) sha256 = Convert.ToHexString(await sha.ComputeHashAsync(fs, ct)).ToLowerInvariant();

        var manifest = new AgentPackageManifest
        {
            Version = version, File = ZipName, SizeBytes = new FileInfo(tmp).Length, Sha256 = sha256,
            Signature = Sign(sha256), PublishedAt = DateTimeOffset.UtcNow,
        };
        lock (_lock)
        {
            File.Copy(tmp, ZipPath, true);
            File.Delete(tmp);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Contracts.Json.SentinelJson.Options));
        }
        _log.LogInformation("Опубликован пакет агента v{Version} ({Size} байт)", version, manifest.SizeBytes);
        return manifest;
    }

    private string Sign(string sha256Hex)
    {
        var key = new Ed25519PrivateKeyParameters(_keys.SigningSeed, 0);
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        var data = Encoding.ASCII.GetBytes(sha256Hex);
        signer.BlockUpdate(data, 0, data.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }
}
