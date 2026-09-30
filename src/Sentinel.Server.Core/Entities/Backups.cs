namespace Sentinel.Server.Core.Entities;

/// <summary>Один запуск задания бэкапа (на одну базу/источник), как его отчитал агент.</summary>
public class BackupRun
{
    public long Id { get; set; }
    public Guid CheckId { get; set; }
    public Guid HostId { get; set; }
    public Guid TenantId { get; set; }
    public string Job { get; set; } = "";
    /// <summary>full | diff | dump</summary>
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
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
    public string? Log { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
