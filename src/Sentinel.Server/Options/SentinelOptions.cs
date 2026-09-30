namespace Sentinel.Server.Options;

public sealed class SentinelOptions
{
    public const string Section = "Sentinel";

    /// <summary>Каталог для ключей и прочих файлов сервера. В Docker — том /data.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Публичный адрес сервера (для ссылок в уведомлениях и инструкций по установке агента).</summary>
    public string PublicUrl { get; set; } = "https://localhost:5001";

    /// <summary>SHA-256 SubjectPublicKeyInfo сертификата (base64), выдаётся агентам для pinning. Пусто — без pinning.</summary>
    public string? CertificatePin { get; set; }

    /// <summary>Корневой сертификат внутреннего CA Caddy (режим без домена). Если файл есть и CertificatePin пуст — pin вычисляется из него.</summary>
    public string CaddyRootCertPath { get; set; } = "/caddy/caddy/pki/authorities/local/root.crt";

    /// <summary>Через сколько секунд без хартбита агент считается офлайн.</summary>
    public int AgentOfflineAfterSeconds { get; set; } = 120;

    public int CommandTtlSeconds { get; set; } = 300;

    /// <summary>Прокси для Telegram Bot API (socks5://host:port или http://host:port), если api.telegram.org недоступен напрямую.</summary>
    public string? TelegramProxy { get; set; }

    /// <summary>Писать лог ещё и в файлы DataDir/logs (включается само, если сервер работает службой Windows).</summary>
    public bool LogToFile { get; set; }

    public TlsOptions Tls { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public JwtOptions Jwt { get; set; } = new();
    public BootstrapOptions Bootstrap { get; set; } = new();
}

/// <summary>Сколько дней хранить историю. Метрики и результаты растут быстрее всего.</summary>
public sealed class RetentionOptions
{
    public int MetricsDays { get; set; } = 90;
    public int CheckResultsDays { get; set; } = 90;
    public int ResolvedIncidentsDays { get; set; } = 365;
    public int NotificationLogDays { get; set; } = 90;
    public int CommandsDays { get; set; } = 180;
    public int BackupRunsDays { get; set; } = 365;
    public int AuditDays { get; set; } = 730;
}

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "sentinel";
    public string Audience { get; set; } = "sentinel-web";
    public int AccessTokenMinutes { get; set; } = 60 * 8;
}

/// <summary>Первый администратор создаётся при пустой таблице пользователей.</summary>
public sealed class BootstrapOptions
{
    public string AdminEmail { get; set; } = "admin@local";
    public string? AdminPassword { get; set; }
}

/// <summary>Собственный HTTPS сервера (без Caddy/nginx): none | self-signed | pfx. Адреса — стандартный параметр Urls.</summary>
public sealed class TlsOptions
{
    public string Mode { get; set; } = "none";
    public string? PfxPath { get; set; }
    public string? PfxPassword { get; set; }
}
