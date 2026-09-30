namespace Sentinel.Server.Core.Entities;

public enum UserRole
{
    /// <summary>Полный доступ, управление пользователями.</summary>
    Admin = 0,
    /// <summary>Инженер: всё, кроме управления пользователями.</summary>
    Engineer = 1,
    /// <summary>Только просмотр всех клиентов.</summary>
    ReadOnly = 2,
    /// <summary>Сотрудник клиента: просмотр только своего тенанта.</summary>
    ClientViewer = 3,
}

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.ReadOnly;
    /// <summary>Для ClientViewer — тенант, который он видит.</summary>
    public Guid? TenantId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Секрет TOTP, зашифрованный ISecretProtector. Задан, но TotpEnabledAt == null — настройка не завершена.</summary>
    public byte[]? TotpSecretEncrypted { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    public bool TotpEnabled => TotpEnabledAt is not null;
}

/// <summary>Неизменяемый журнал действий людей и агентов.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? TenantId { get; set; }
    /// <summary>"command.issue", "check.update", "user.login", "agent.enroll" ...</summary>
    public string Action { get; set; } = "";
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? DetailsJson { get; set; }
    public string? RemoteIp { get; set; }
}
