using Sentinel.Contracts.Protocol;

namespace Sentinel.Server.Core.Entities;

/// <summary>Экземпляр модуля, настроенный на хосте. Отправляется агенту как CheckConfig.</summary>
public class Check
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public Host? Host { get; set; }

    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>Настройки модуля (jsonb).</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>Антифлаппинг: сколько подряд результатов с новым статусом нужно для смены состояния. 1 — сразу.</summary>
    public int ConfirmCount { get; set; } = 2;
    /// <summary>
    /// Ключи, которые не отслеживаются (CPU в «Системе», конкретный диск, служба): результаты по ним отбрасываются на сервере.
    /// Работает для любого модуля и не требует обновления агента.
    /// </summary>
    public List<string> IgnoredKeys { get; set; } = new();
    /// <summary>
    /// Задержка оповещения, мин: инцидент открывается, только если проблема держится дольше.
    /// Кратковременные пики CPU/памяти видны на дашборде, но не шлют уведомлений. 0 — сразу.
    /// </summary>
    public int AlertDelayMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<CheckState> States { get; set; } = new();
}

/// <summary>Текущее состояние по каждому ключу проверки (служба, диск, хост).</summary>
public class CheckState
{
    public Guid Id { get; set; }
    public Guid CheckId { get; set; }
    public Check? Check { get; set; }
    public string Key { get; set; } = "";

    public CheckStatus Status { get; set; } = CheckStatus.Unknown;
    public CheckStatus PreviousStatus { get; set; } = CheckStatus.Unknown;
    public string Summary { get; set; } = "";
    public string? DetailsJson { get; set; }

    public DateTimeOffset? LastResultAt { get; set; }
    /// <summary>Когда статус последний раз менялся — для длительности инцидента.</summary>
    public DateTimeOffset? LastChangeAt { get; set; }
    /// <summary>С какого момента ключ непрерывно не в порядке (смена warning↔critical не сбрасывает) — для задержки оповещения.</summary>
    public DateTimeOffset? ProblemSince { get; set; }

    /// <summary>Антифлаппинг: статус-кандидат и сколько раз подряд он пришёл.</summary>
    public CheckStatus? PendingStatus { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>История результатов. Ретеншн — отдельной фоновой задачей.</summary>
public class CheckResultRecord
{
    public long Id { get; set; }
    public Guid CheckId { get; set; }
    public string Key { get; set; } = "";
    public CheckStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Числовые ряды для графиков.</summary>
public class MetricPoint
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid CheckId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public double Value { get; set; }
}
