using Microsoft.EntityFrameworkCore;
using Sentinel.Server.Core.Entities;

namespace Sentinel.Server.Data;

public class SentinelDbContext(DbContextOptions<SentinelDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Host> Hosts => Set<Host>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Check> Checks => Set<Check>();
    public DbSet<CheckState> CheckStates => Set<CheckState>();
    public DbSet<CheckResultRecord> CheckResults => Set<CheckResultRecord>();
    public DbSet<MetricPoint> Metrics => Set<MetricPoint>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationRoute> NotificationRoutes => Set<NotificationRoute>();
    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();
    public DbSet<NotificationLog> NotificationLog => Set<NotificationLog>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<MaintenanceWindow> MaintenanceWindows => Set<MaintenanceWindow>();
    public DbSet<CheckTemplate> CheckTemplates => Set<CheckTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Slug).HasMaxLength(64);
        });

        b.Entity<Site>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Tenant).WithMany(t => t.Sites).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Host>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Name });
            e.HasOne(x => x.Tenant).WithMany(t => t.Hosts).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => x.HostId).IsUnique();
            e.HasOne(x => x.Host).WithOne(h => h.Agent).HasForeignKey<Agent>(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Check>(e =>
        {
            e.Property(x => x.ModuleId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.SettingsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Host).WithMany(h => h.Checks).HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckTemplate>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ItemsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckState>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(400);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.CheckId, x.Key }).IsUnique();
            e.HasOne(x => x.Check).WithMany(c => c.States).HasForeignKey(x => x.CheckId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckResultRecord>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(400);
            e.HasIndex(x => new { x.CheckId, x.Key, x.At });
            e.HasIndex(x => x.ReceivedAt);
        });

        b.Entity<MetricPoint>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(400);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.CheckId, x.Key, x.Name, x.Time });
            e.HasIndex(x => x.Time);
        });

        b.Entity<Command>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.AgentId, x.IssuedAt });
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Ignore(x => x.TotpEnabled);
        });

        b.Entity<Incident>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(400);
            e.Property(x => x.Title).HasMaxLength(400);
            e.HasIndex(x => new { x.Status, x.OpenedAt });
            e.HasIndex(x => new { x.HostId, x.CheckId, x.Key, x.Status });
            e.HasIndex(x => x.TenantId);
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IncidentEvent>(e =>
        {
            e.Property(x => x.Type).HasMaxLength(32);
            e.HasIndex(x => new { x.IncidentId, x.At });
            e.HasOne<Incident>().WithMany(i => i.Events).HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NotificationChannel>(e => e.Property(x => x.Name).HasMaxLength(200));

        b.Entity<NotificationRoute>(e =>
        {
            e.HasOne(x => x.Channel).WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<NotificationOutbox>(e => e.HasIndex(x => x.ProcessedAt));
        b.Entity<NotificationLog>(e => e.HasIndex(x => new { x.IncidentId, x.At }));

        b.Entity<BackupRun>(e =>
        {
            e.Property(x => x.Job).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Target).HasMaxLength(200);
            e.HasIndex(x => new { x.CheckId, x.Target, x.FinishedAt });
            e.HasIndex(x => x.TenantId);
        });

        b.Entity<MaintenanceWindow>(e =>
        {
            e.Property(x => x.Comment).HasMaxLength(400);
            e.HasIndex(x => x.EndsAt);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.TenantId);
        });
    }
}
