namespace Sentinel.Server.Core.Entities;

/// <summary>Установленный у клиента агент. Ровно один на хост.</summary>
public class Agent
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public Host? Host { get; set; }

    /// <summary>HMAC-секрет, зашифрованный серверным ключом (см. ISecretProtector).</summary>
    public byte[] SecretEncrypted { get; set; } = Array.Empty<byte>();

    public string AgentVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string MachineId { get; set; } = "";
    public bool Is64Bit { get; set; }

    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool IsOnline { get; set; }
    /// <summary>Версия конфига, которую агент подтвердил последним хартбитом.</summary>
    public int ReportedConfigVersion { get; set; }
    public int ReportedQueueDepth { get; set; }
}

/// <summary>Одноразовый токен для регистрации агента.</summary>
public class EnrollmentToken
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }
    /// <summary>Если задан — агент привяжется к этому хосту; иначе хост создаётся по имени машины.</summary>
    public Guid? HostId { get; set; }
    public Guid? SiteId { get; set; }

    /// <summary>SHA-256 токена; сам токен показывается один раз при создании.</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByAgentId { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? Comment { get; set; }
}

public enum CommandStatus
{
    Pending = 0,
    Sent = 1,
    Succeeded = 2,
    Failed = 3,
    Expired = 4,
}

/// <summary>Команда, отправленная агенту, и её результат.</summary>
public class Command
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string Type { get; set; } = "";
    public string? PayloadJson { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public CommandStatus Status { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
