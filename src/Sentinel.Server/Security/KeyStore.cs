using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Sentinel.Server.Options;

namespace Sentinel.Server.Security;

/// <summary>
/// Ключи сервера. Приоритет: переменная окружения (base64) → файл в DataDir/keys → генерация нового.
/// Файлы создаются один раз; их потеря = потеря доступа ко всем секретам в БД и доверия агентов.
/// </summary>
public sealed class KeyStore
{
    private readonly string _dir;

    public byte[] DataKey { get; }        // AES-256-GCM для секретов в БД
    public byte[] JwtKey { get; }         // HMAC-SHA256 для токенов пользователей
    public byte[] SigningSeed { get; }    // Ed25519 seed для подписи команд

    public KeyStore(IOptions<SentinelOptions> options, ILogger<KeyStore> log)
    {
        _dir = Path.Combine(options.Value.DataDir, "keys");
        Directory.CreateDirectory(_dir);

        DataKey = Load("SENTINEL_DATA_KEY", "data.key", 32, log);
        JwtKey = Load("SENTINEL_JWT_KEY", "jwt.key", 64, log);
        SigningSeed = Load("SENTINEL_SIGNING_KEY", "signing.key", 32, log);
    }

    private byte[] Load(string envVar, string fileName, int length, ILogger log)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            var bytes = Convert.FromBase64String(env.Trim());
            if (bytes.Length != length)
                throw new InvalidOperationException($"{envVar}: ожидается {length} байт в base64, получено {bytes.Length}.");
            return bytes;
        }

        var path = Path.Combine(_dir, fileName);
        if (File.Exists(path))
        {
            var bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (bytes.Length != length)
                throw new InvalidOperationException($"{path}: повреждён (длина {bytes.Length}, ожидается {length}).");
            return bytes;
        }

        var fresh = RandomNumberGenerator.GetBytes(length);
        File.WriteAllText(path, Convert.ToBase64String(fresh));
        log.LogWarning("Сгенерирован новый ключ {File}. Сохраните каталог {Dir} в резервную копию.", fileName, _dir);
        return fresh;
    }
}
