using System;
using System.Security.Cryptography;
using System.Text;

namespace Sentinel.Contracts.Security;

/// <summary>
/// HMAC-аутентификация запросов агента. Секрет никогда не передаётся по сети после enrollment.
/// Подписывается: метод, путь, unix-время, nonce и SHA-256 тела.
/// </summary>
public static class AgentAuth
{
    public const string HeaderAgentId = "X-Agent-Id";
    public const string HeaderTimestamp = "X-Timestamp";
    public const string HeaderNonce = "X-Nonce";
    public const string HeaderSignature = "X-Signature";

    /// <summary>Допустимое расхождение часов агента и сервера.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static string CanonicalString(string method, string path, long unixTime, string nonce, string bodySha256Hex)
        => string.Join("\n", method.ToUpperInvariant(), path, unixTime.ToString(), nonce, bodySha256Hex);

    public static string Sign(byte[] secret, string canonical)
    {
        using (var hmac = new HMACSHA256(secret))
        {
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }
    }

    public static bool Verify(byte[] secret, string canonical, string signatureBase64)
    {
        byte[] expected;
        using (var hmac = new HMACSHA256(secret))
        {
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        }

        byte[] actual;
        try { actual = Convert.FromBase64String(signatureBase64); }
        catch (FormatException) { return false; }

        return FixedTimeEquals(expected, actual);
    }

    public static string Sha256Hex(byte[] data)
    {
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(data ?? Array.Empty<byte>());
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public static string NewNonce()
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
