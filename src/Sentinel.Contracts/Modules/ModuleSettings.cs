using System.Collections.Generic;

namespace Sentinel.Contracts.Modules;

/// <summary>Настройки модуля <c>services</c>: контроль служб Windows.</summary>
public sealed class ServicesModuleSettings
{
    public List<ServiceTarget> Services { get; set; } = new List<ServiceTarget>();
}

public sealed class ServiceTarget
{
    /// <summary>Системное имя службы (MSSQLSERVER, 1C:Enterprise 8.3 Server Agent ...).</summary>
    public string Name { get; set; } = "";
    /// <summary>Пытаться запустить, если остановлена.</summary>
    public bool AutoRestart { get; set; }
    /// <summary>Остановленная служба — критично (иначе предупреждение).</summary>
    public bool Critical { get; set; } = true;
}

/// <summary>Настройки модуля <c>disks</c>: свободное место на локальных дисках.</summary>
public sealed class DisksModuleSettings
{
    public double WarnFreePercent { get; set; } = 15;
    public double CritFreePercent { get; set; } = 7;
    /// <summary>Дополнительно: предупреждение, если свободно меньше N ГБ (0 — не учитывать).</summary>
    public double WarnFreeGb { get; set; } = 0;
    public double CritFreeGb { get; set; } = 0;
    /// <summary>Буквы дисков для исключения: ["D:"].</summary>
    public List<string> Exclude { get; set; } = new List<string>();
    /// <summary>Если задано — проверяются только эти диски.</summary>
    public List<string> Include { get; set; } = new List<string>();
}

/// <summary>Настройки модуля <c>eventlog</c>: критичные ошибки в журналах Windows.</summary>
public sealed class EventLogModuleSettings
{
    public List<string> Logs { get; set; } = new List<string> { "System", "Application" };
    /// <summary>Уровни: 1 — Critical, 2 — Error, 3 — Warning.</summary>
    public List<int> Levels { get; set; } = new List<int> { 1, 2 };
    /// <summary>Окно, за которое считаются события. 0 — равно интервалу проверки.</summary>
    public int LookbackMinutes { get; set; } = 0;
    public List<string> IncludeSources { get; set; } = new List<string>();
    public List<string> ExcludeSources { get; set; } = new List<string>();
    public List<int> IncludeEventIds { get; set; } = new List<int>();
    public List<int> ExcludeEventIds { get; set; } = new List<int>();
    /// <summary>Сколько событий — Warning, сколько — Critical.</summary>
    public int WarnCount { get; set; } = 1;
    public int CritCount { get; set; } = 10;
    public int MaxEventsInDetails { get; set; } = 30;
}

/// <summary>Настройки модуля <c>net.ping</c>.</summary>
public sealed class PingModuleSettings
{
    public List<PingTarget> Targets { get; set; } = new List<PingTarget>();
    public int TimeoutMs { get; set; } = 1500;
    public int Attempts { get; set; } = 3;
    /// <summary>Предупреждение при задержке выше, мс (0 — не учитывать).</summary>
    public int WarnLatencyMs { get; set; } = 0;
}

public sealed class PingTarget
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Critical { get; set; } = true;
}

/// <summary>Настройки модуля <c>net.tcp</c>: доступность порта, опционально TLS и срок сертификата.</summary>
public sealed class TcpModuleSettings
{
    public List<TcpTarget> Targets { get; set; } = new List<TcpTarget>();
    public int TimeoutMs { get; set; } = 3000;
}

public sealed class TcpTarget
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public bool Tls { get; set; }
    /// <summary>Предупреждение, если сертификат истекает раньше, чем через N дней.</summary>
    public int CertWarnDays { get; set; } = 14;
    public bool Critical { get; set; } = true;
}
