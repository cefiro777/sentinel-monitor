using Sentinel.Contracts.Protocol;

namespace Sentinel.Server.Core.Abstractions;

/// <summary>Шифрование секретов, хранящихся в БД (секреты агентов, пароли в настройках модулей).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Подпись команд приватным ключом сервера (Ed25519).</summary>
public interface ICommandSigner
{
    /// <summary>Публичный ключ (base64) — выдаётся агенту при enrollment.</summary>
    string PublicKeyBase64 { get; }
    void Sign(CommandEnvelope envelope);
}

/// <summary>Доставка сообщений подключённым агентам (реализуется поверх SignalR).</summary>
public interface IAgentMessenger
{
    Task<bool> SendCommandAsync(Guid agentId, CommandEnvelope envelope, CancellationToken ct = default);
    Task NotifyConfigChangedAsync(Guid agentId, int version, CancellationToken ct = default);
    bool IsConnected(Guid agentId);
}

/// <summary>Хеширование паролей пользователей.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}
