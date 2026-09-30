using Sentinel.Contracts.Protocol;

namespace Sentinel.Server.Core.Entities;

public enum IncidentStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2,
    /// <summary>Взят в работу до срока (SnoozedUntil): не шумит уведомлениями и не красит хост, пока срок не вышел.</summary>
    InProgress = 3,
}

public enum IncidentKind
{
    /// <summary>Проверка в состоянии Warning/Critical/Unknown.</summary>
    CheckState = 0,
    /// <summary>Агент не выходит на связь.</summary>
    AgentOffline = 1,
    /// <summary>Бэкап не пришёл вовремя (этап 3).</summary>
    BackupOverdue = 2,
}

/// <summary>Инцидент: открывается при переходе проверки в проблемное состояние, закрывается при возврате в OK.</summary>
public class Incident
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid HostId { get; set; }
    public Host? Host { get; set; }
    public Guid? CheckId { get; set; }
    public string Key { get; set; } = "";
    public IncidentKind Kind { get; set; }

    /// <summary>Текущая серьёзность: Warning / Critical / Unknown.</summary>
    public CheckStatus Severity { get; set; }
    public IncidentStatus Status { get; set; }

    /// <summary>«Диск C: на SRV-1C», «Служба MSSQLSERVER на SRV-1C».</summary>
    public string Title { get; set; } = "";
    /// <summary>Последнее описание состояния.</summary>
    public string Summary { get; set; } = "";

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Взят в работу до этого момента: уведомления молчат, в сводках — отдельной категорией. После срока инцидент снова открывается.</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }
    public Guid? InProgressByUserId { get; set; }

    /// <summary>Напоминания о неподтверждённом инциденте.</summary>
    public DateTimeOffset? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }

    public List<IncidentEvent> Events { get; set; } = new();
}

public class IncidentEvent
{
    public long Id { get; set; }
    public Guid IncidentId { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>opened, severity, acknowledged, comment, resolved, notified</summary>
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public Guid? UserId { get; set; }
}

public enum ChannelType
{
    Email = 0,
    Telegram = 1,
    Max = 2,
}

/// <summary>Канал доставки уведомлений. Настройки (SMTP-пароль, токен бота) хранятся зашифрованными.</summary>
public class NotificationChannel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public ChannelType Type { get; set; }
    /// <summary>JSON настроек канала, зашифрованный ISecretProtector.</summary>
    public byte[] SettingsEncrypted { get; set; } = Array.Empty<byte>();
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Маршрут: какие инциденты каких клиентов в какой канал.</summary>
public class NotificationRoute
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public NotificationChannel? Channel { get; set; }
    /// <summary>null — все клиенты.</summary>
    public Guid? TenantId { get; set; }
    public CheckStatus MinSeverity { get; set; } = CheckStatus.Warning;
    public bool NotifyOnResolve { get; set; } = true;
    /// <summary>Тихие часы (локальное время сервера), например 23–7. null — без ограничений.</summary>
    public int? QuietFromHour { get; set; }
    public int? QuietToHour { get; set; }
    /// <summary>Повторять уведомление каждые N минут, пока инцидент не взят в работу (0 — не повторять).</summary>
    public int RemindMinutes { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>Окно обслуживания: на это время уведомления по хосту/клиенту подавляются, офлайн агента — не инцидент.</summary>
public class MaintenanceWindow
{
    public Guid Id { get; set; }
    /// <summary>Либо клиент целиком, либо конкретный хост.</summary>
    public Guid? TenantId { get; set; }
    public Guid? HostId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Comment { get; set; } = "";
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum NotificationKind
{
    Opened = 0,
    Resolved = 1,
    Escalated = 2,
    Reminder = 3,
    Test = 9,
}

/// <summary>Очередь исходящих уведомлений: пишется вместе с изменением инцидента, разбирается фоновым воркером.</summary>
public class NotificationOutbox
{
    public long Id { get; set; }
    public Guid IncidentId { get; set; }
    public NotificationKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public class NotificationLog
{
    public long Id { get; set; }
    public Guid IncidentId { get; set; }
    public Guid ChannelId { get; set; }
    public NotificationKind Kind { get; set; }
    public DateTimeOffset At { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
