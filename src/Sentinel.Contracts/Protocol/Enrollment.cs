using System;

namespace Sentinel.Contracts.Protocol;

/// <summary>Регистрация агента по одноразовому токену.</summary>
public sealed class EnrollRequest
{
    public string Token { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    /// <summary>Стабильный идентификатор машины (MachineGuid из реестра).</summary>
    public string MachineId { get; set; } = "";
    public bool Is64Bit { get; set; }
}

public sealed class EnrollResponse
{
    public Guid AgentId { get; set; }
    public Guid HostId { get; set; }
    /// <summary>Секрет для HMAC-подписи запросов (base64). Показывается один раз.</summary>
    public string Secret { get; set; } = "";
    /// <summary>Публичный ключ сервера (Ed25519, base64) для проверки подписи команд.</summary>
    public string ServerPublicKey { get; set; } = "";
    /// <summary>SHA-256 от SubjectPublicKeyInfo сертификата сервера (base64) для pinning. Пусто — не пинить.</summary>
    public string? CertificatePin { get; set; }
}
