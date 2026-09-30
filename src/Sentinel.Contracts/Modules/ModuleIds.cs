namespace Sentinel.Contracts.Modules;

/// <summary>Идентификаторы модулей агента. Совпадают на сервере (схемы, отображение) и на агенте (реализация).</summary>
public static class ModuleIds
{
    public const string System = "system";
    public const string Services = "services";
    public const string Disks = "disks";
    public const string EventLog = "eventlog";
    public const string NetPing = "net.ping";
    public const string NetTcp = "net.tcp";
    public const string BackupFiles = "backup.files";
    public const string Backup1CFile = "backup.1c.file";
    public const string BackupMsSql = "backup.mssql";
    public const string BackupPostgres = "backup.postgres";
    public const string BackupCloud = "backup.cloud";
    public const string CctvOnvif = "cctv.onvif";
    public const string CctvRtsp = "cctv.rtsp";
    public const string CctvHikvision = "cctv.hikvision";
    public const string CctvDahua = "cctv.dahua";
    public const string VmHyperV = "vm.hyperv";
    public const string RemoteExec = "remote.exec";
}

/// <summary>Настройки модуля <c>system</c>.</summary>
public sealed class SystemModuleSettings
{
    public int CpuWarnPercent { get; set; } = 85;
    public int CpuCritPercent { get; set; } = 95;
    public int MemoryWarnPercent { get; set; } = 85;
    public int MemoryCritPercent { get; set; } = 95;
    /// <summary>Допустимое расхождение часов с сервером, секунд.</summary>
    public int ClockDriftWarnSeconds { get; set; } = 60;
    public bool WarnOnPendingReboot { get; set; } = true;
}
