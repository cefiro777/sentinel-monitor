using System;
using System.Collections.Generic;

namespace Sentinel.Contracts.Modules;

/// <summary>Расписание задания. Бэкапы запускаются по расписанию, а не по интервалу проверки.</summary>
public sealed class BackupSchedule
{
    /// <summary>daily | hourly | weekly</summary>
    public string Type { get; set; } = "daily";
    /// <summary>Время запуска для daily/weekly, локальное время сервера: "02:00".</summary>
    public string Time { get; set; } = "02:00";
    /// <summary>Для hourly: каждые N часов.</summary>
    public int EveryHours { get; set; } = 6;
    /// <summary>Для weekly: дни недели 1 (пн) … 7 (вс).</summary>
    public List<int> DaysOfWeek { get; set; } = new List<int> { 1, 2, 3, 4, 5 };

    /// <summary>Ожидаемый период между запусками — сервер по нему решает, что бэкап просрочен.</summary>
    public TimeSpan ExpectedPeriod()
    {
        switch ((Type ?? "daily").ToLowerInvariant())
        {
            case "hourly": return TimeSpan.FromHours(Math.Max(1, EveryHours));
            case "weekly": return DaysOfWeek.Count >= 5 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
            default: return TimeSpan.FromDays(1);
        }
    }

    /// <summary>Следующий запуск после <paramref name="lastRun"/> (локальное время).</summary>
    public DateTimeOffset NextRun(DateTimeOffset? lastRun, DateTimeOffset now)
    {
        var type = (Type ?? "daily").ToLowerInvariant();
        if (type == "hourly")
        {
            var every = TimeSpan.FromHours(Math.Max(1, EveryHours));
            return lastRun is null ? now : lastRun.Value + every;
        }

        if (!TimeSpan.TryParse(Time, out var at)) at = new TimeSpan(2, 0, 0);
        var local = now.ToLocalTime();
        var candidate = new DateTimeOffset(local.Date + at, local.Offset);
        // Ищем ближайший слот, который позже последнего запуска (с запасом, чтобы не запускаться дважды в одну минуту).
        var floor = lastRun is null ? now.ToLocalTime().AddMinutes(-1) : lastRun.Value.ToLocalTime().AddMinutes(1);
        if (candidate <= floor) candidate = candidate.AddDays(1);
        // Если последнего запуска не было и слот сегодня уже прошёл — запускаем сразу (первый бэкап не ждёт до ночи).
        if (lastRun is null && candidate > local && candidate - local > TimeSpan.FromHours(1) && local > new DateTimeOffset(local.Date + at, local.Offset))
            return now;

        if (type == "weekly")
        {
            var days = DaysOfWeek.Count == 0 ? new List<int> { 1, 2, 3, 4, 5 } : DaysOfWeek;
            for (var i = 0; i < 8 && !days.Contains(IsoDay(candidate.DayOfWeek)); i++) candidate = candidate.AddDays(1);
        }
        return candidate;
    }

    private static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
}

/// <summary>Куда складывать артефакты. Локальный путь или UNC-шара; для шары можно задать учётку.</summary>
public sealed class BackupDestination
{
    public string Path { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class BackupRetention
{
    /// <summary>Сколько полных наборов (полный + его разностные) хранить локально.</summary>
    public int KeepFullSets { get; set; } = 4;
    /// <summary>Сколько хранить в облаке (число файлов задания). 0 — не удалять.</summary>
    public int CloudKeepCount { get; set; } = 30;
}

/// <summary>Выгрузка в облако. Сейчас — Яндекс.Диск (OAuth-токен приложения).</summary>
public sealed class BackupCloud
{
    public bool Enabled { get; set; }
    /// <summary>yandex</summary>
    public string Provider { get; set; } = "yandex";
    public string Token { get; set; } = "";
    /// <summary>Корень в облаке; папки {tenant}/{host}/{job} добавляются автоматически.</summary>
    public string RootFolder { get; set; } = "/Sentinel";
    /// <summary>Сверять хеш после загрузки (обязательно для «успешно»).</summary>
    public bool VerifyHash { get; set; } = true;
    /// <summary>Предупреждение, если на Диске свободно меньше N ГБ (0 — не проверять).</summary>
    public double WarnFreeGb { get; set; } = 5;
}

/// <summary>Общая часть всех заданий бэкапа.</summary>
public abstract class BackupJobSettingsBase
{
    /// <summary>Короткое имя для файлов и папок: "buh-1c". Пусто — из названия проверки.</summary>
    public string JobSlug { get; set; } = "";
    public BackupSchedule Schedule { get; set; } = new BackupSchedule();
    public BackupDestination Destination { get; set; } = new BackupDestination();
    public BackupRetention Retention { get; set; } = new BackupRetention();
    public BackupCloud Cloud { get; set; } = new BackupCloud();
}

/// <summary>backup.files — папки/файлы в zip: полный раз в N дней, между ними разностные (изменённые с последнего полного).</summary>
public sealed class FilesBackupSettings : BackupJobSettingsBase
{
    /// <summary>zip — архивы (полный + разностные); mirror — актуальная копия папки, как robocopy /MIR.</summary>
    public string Mode { get; set; } = "zip";
    /// <summary>mirror: удалять в копии то, чего больше нет в источнике (иначе копия только пополняется).</summary>
    public bool MirrorDeleteRemoved { get; set; } = true;
    /// <summary>
    /// mirror: сколько дней держать в копии файл, пропавший из источника, прежде чем удалить его.
    /// 0 — удалять сразу. Даёт время заметить случайное удаление и забрать файл из копии.
    /// </summary>
    public int MirrorDeleteAfterDays { get; set; } = 14;
    /// <summary>
    /// mirror: что делать с файлами, которые скопировать не удалось.
    /// fail — задание считается неуспешным (по умолчанию); skip — файлы пропускаются, задание успешно с предупреждением.
    /// </summary>
    public string MirrorOnError { get; set; } = "fail";
    /// <summary>
    /// mirror: помнить непереносимые файлы и не пытаться копировать их снова, пока они не изменятся в источнике.
    /// Экономит время на больших шарах, где часть файлов не копируется принципиально (права, длина пути, блокировки).
    /// </summary>
    public bool MirrorRememberSkipped { get; set; } = true;
    public List<string> Sources { get; set; } = new List<string>();
    /// <summary>Маски исключений: *.tmp, \\Temp\\, thumbs.db</summary>
    public List<string> Exclude { get; set; } = new List<string>();
    /// <summary>Полный бэкап каждые N дней, иначе разностный. 1 — всегда полный.</summary>
    public int FullEveryDays { get; set; } = 7;
    /// <summary>Снимок VSS — для открытых/заблокированных файлов.</summary>
    public bool UseVss { get; set; } = true;
}

/// <summary>backup.1c.file — файловая база 1С (1Cv8.1CD) через VSS с проверкой заголовка.</summary>
public sealed class OneCFileBackupSettings : BackupJobSettingsBase
{
    /// <summary>Папка базы (где лежит 1Cv8.1CD).</summary>
    public string BasePath { get; set; } = "";
    /// <summary>Копировать всю папку (внешние обработки, настройки), а не только 1Cv8.1CD.</summary>
    public bool WholeFolder { get; set; } = false;
    public int FullEveryDays { get; set; } = 1;
}

/// <summary>backup.mssql — BACKUP DATABASE через T-SQL, полный раз в N дней + разностные, RESTORE VERIFYONLY.</summary>
public sealed class MsSqlBackupSettings : BackupJobSettingsBase
{
    /// <summary>Экземпляр: ".", ".\SQLEXPRESS", "srv\1C".</summary>
    public string Server { get; set; } = ".";
    public List<string> Databases { get; set; } = new List<string>();
    public bool IntegratedSecurity { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>Каталог, куда SQL Server пишет .bak (путь с точки зрения службы SQL Server). Пусто — Destination.Path.</summary>
    public string? SqlBackupDir { get; set; }
    public int FullEveryDays { get; set; } = 7;
    public bool Verify { get; set; } = true;
    public bool Compression { get; set; } = true;
}

/// <summary>backup.postgres — pg_dump в custom-формате, проверка через pg_restore --list.</summary>
public sealed class PostgresBackupSettings : BackupJobSettingsBase
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public List<string> Databases { get; set; } = new List<string>();
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
    /// <summary>Папка с pg_dump.exe / pg_restore.exe. Пусто — ищем в PATH и стандартных путях PostgreSQL / 1C.</summary>
    public string? BinDir { get; set; }
}

/// <summary>Отчёт о выполнении бэкапа — передаётся в CheckResult.Details, сервер сохраняет его как BackupRun.</summary>
public sealed class BackupRunInfo
{
    public string Job { get; set; } = "";
    /// <summary>full | diff | dump</summary>
    public string Kind { get; set; } = "";
    /// <summary>База/источник, если задание обслуживает несколько.</summary>
    public string? Target { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Success { get; set; }
    public string? ArtifactPath { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public bool Verified { get; set; }
    public bool CloudEnabled { get; set; }
    public bool CloudUploaded { get; set; }
    public string? CloudPath { get; set; }
    public string? Error { get; set; }
    public List<string> Log { get; set; } = new List<string>();
}
