using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Abstractions;

namespace Sentinel.Server.Security;

/// <summary>AES-256-GCM: nonce(12) | tag(16) | ciphertext.</summary>
public sealed class AesGcmSecretProtector(KeyStore keys) : ISecretProtector
{
    private const int NonceSize = 12, TagSize = 16;

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(keys.DataKey, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        var result = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Повреждённый секрет.");
        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(keys.DataKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

/// <summary>Ed25519-подпись команд. Публичный ключ раздаётся агентам при enrollment.</summary>
public sealed class Ed25519CommandSigner : ICommandSigner
{
    private readonly Ed25519PrivateKeyParameters _private;

    public Ed25519CommandSigner(KeyStore keys)
    {
        _private = new Ed25519PrivateKeyParameters(keys.SigningSeed, 0);
        PublicKeyBase64 = Convert.ToBase64String(_private.GeneratePublicKey().GetEncoded());
    }

    public string PublicKeyBase64 { get; }

    public void Sign(CommandEnvelope envelope)
    {
        var data = Encoding.UTF8.GetBytes(envelope.CanonicalString());
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        signer.BlockUpdate(data, 0, data.Length);
        envelope.Signature = Convert.ToBase64String(signer.GenerateSignature());
    }
}

/// <summary>Argon2id. Формат: argon2id$iterations$memoryKb$parallelism$salt$hash (base64).</summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 3, MemoryKb = 64 * 1024, Parallelism = 2, HashSize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, Iterations, MemoryKb, Parallelism);
        return $"argon2id${Iterations}${MemoryKb}${Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[0] != "argon2id") return false;
        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        var actual = Compute(password, salt, int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Compute(string password, byte[] salt, int iterations, int memoryKb, int parallelism)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            Iterations = iterations,
            MemorySize = memoryKb,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(HashSize);
    }
}
