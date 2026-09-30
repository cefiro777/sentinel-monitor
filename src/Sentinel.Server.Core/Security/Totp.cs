using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Server.Core.Security;

/// <summary>TOTP по RFC 6238 (HMAC-SHA1, 30 с, 6 цифр) — совместимо с Google Authenticator, Яндекс.Ключ, Aegis и т.п.</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public static string ComputeCode(byte[] secret, long timeStep)
    {
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(timeStep & 0xff); timeStep >>= 8; }
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static long CurrentStep(DateTimeOffset? now = null) => (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / StepSeconds;

    /// <summary>Проверяет код в окне ±window шагов. Возвращает шаг, на котором совпало (для защиты от повторного использования), либо null.</summary>
    public static long? Verify(byte[] secret, string code, int window = 1, DateTimeOffset? now = null)
    {
        code = (code ?? "").Trim().Replace(" ", "");
        if (code.Length != Digits || !code.All(char.IsDigit)) return null;
        var step = CurrentStep(now);
        for (var d = -window; d <= window; d++)
        {
            var candidate = ComputeCode(secret, step + d);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(code)))
                return step + d;
        }
        return null;
    }

    /// <summary>otpauth://-ссылка для QR-кода.</summary>
    public static string BuildUri(byte[] secret, string account, string issuer = "Sentinel")
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
