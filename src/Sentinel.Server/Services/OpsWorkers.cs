using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>Напоминания о неподтверждённых инцидентах по маршрутам с RemindMinutes &gt; 0.</summary>
public sealed class IncidentReminderWorker(IServiceScopeFactory scopes, ILogger<IncidentReminderWorker> log) : BackgroundService
{
    private const int MaxReminders = 20;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
                var now = DateTimeOffset.UtcNow;

                // Инциденты, у которых истёк срок «в работе», возвращаем в открытые — с уведомлением.
                var expired = await db.Incidents.Where(i => i.Status == IncidentStatus.InProgress && i.SnoozedUntil != null && i.SnoozedUntil <= now).ToListAsync(ct);
                if (expired.Count > 0)
                {
                    var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
                    foreach (var i in expired) incidents.ExpireInProgress(i, now);
                    await db.SaveChangesAsync(ct);
                }

                var routes = await db.NotificationRoutes.Where(r => r.IsEnabled && r.RemindMinutes > 0).ToListAsync(ct);
                if (routes.Count == 0) continue;

                var open = await db.Incidents.Where(i => i.Status == IncidentStatus.Open && i.ReminderCount < MaxReminders).ToListAsync(ct);
                var enqueued = 0;
                foreach (var i in open)
                {
                    // Самый короткий подходящий интервал среди маршрутов этого клиента.
                    var interval = routes.Where(r => (r.TenantId == null || r.TenantId == i.TenantId) && i.Severity >= r.MinSeverity).Select(r => r.RemindMinutes).DefaultIfEmpty(0).Min();
                    if (interval <= 0) continue;
                    var since = i.LastReminderAt ?? i.OpenedAt;
                    if (now - since < TimeSpan.FromMinutes(interval)) continue;

                    i.LastReminderAt = now;
                    i.ReminderCount++;
                    db.NotificationOutbox.Add(new NotificationOutbox { IncidentId = i.Id, Kind = NotificationKind.Reminder, CreatedAt = now });
                    enqueued++;
                }
                if (enqueued > 0) { await db.SaveChangesAsync(ct); log.LogInformation("Напоминаний поставлено в очередь: {Count}", enqueued); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Ошибка воркера напоминаний"); }
        }
    }
}

/// <summary>Ежедневная чистка истории по срокам из настроек Retention.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<SentinelOptions> options, ILogger<RetentionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CleanAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Ошибка ретеншна"); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CleanAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var r = options.Value.Retention;
        var now = DateTimeOffset.UtcNow;
        var tMetrics = now.AddDays(-Math.Max(1, r.MetricsDays));
        var tResults = now.AddDays(-Math.Max(1, r.CheckResultsDays));
        var tIncidents = now.AddDays(-Math.Max(1, r.ResolvedIncidentsDays));
        var tNotif = now.AddDays(-Math.Max(1, r.NotificationLogDays));
        var tOutbox = now.AddDays(-7);
        var tCommands = now.AddDays(-Math.Max(1, r.CommandsDays));
        var tBackups = now.AddDays(-Math.Max(1, r.BackupRunsDays));
        var tAudit = now.AddDays(-Math.Max(1, r.AuditDays));
        var tWindows = now.AddDays(-30);

        // Инциденты проверок, которых уже нет (удалены до появления авто-закрытия) — иначе висят в «активных» вечно.
        var orphans = await db.Incidents
            .Where(i => i.Status != IncidentStatus.Resolved && i.CheckId != null && !db.Checks.Any(c => c.Id == i.CheckId))
            .ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, IncidentStatus.Resolved)
                .SetProperty(i => i.ResolvedAt, now)
                .SetProperty(i => i.UpdatedAt, now)
                .SetProperty(i => i.Summary, "Проверка удалена"), ct);
        if (orphans > 0) log.LogInformation("Закрыты инциденты удалённых проверок: {Count}", orphans);

        var metrics = await db.Metrics.Where(m => m.Time < tMetrics).ExecuteDeleteAsync(ct);
        var results = await db.CheckResults.Where(x => x.At < tResults).ExecuteDeleteAsync(ct);
        var incidents = await db.Incidents.Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt < tIncidents).ExecuteDeleteAsync(ct);
        var notif = await db.NotificationLog.Where(n => n.At < tNotif).ExecuteDeleteAsync(ct);
        var outbox = await db.NotificationOutbox.Where(o => o.ProcessedAt != null && o.ProcessedAt < tOutbox).ExecuteDeleteAsync(ct);
        var commands = await db.Commands.Where(c => c.IssuedAt < tCommands).ExecuteDeleteAsync(ct);
        var backups = await db.BackupRuns.Where(b => b.FinishedAt < tBackups).ExecuteDeleteAsync(ct);
        var audit = await db.AuditLog.Where(a => a.At < tAudit).ExecuteDeleteAsync(ct);
        var windows = await db.MaintenanceWindows.Where(m => m.EndsAt < tWindows).ExecuteDeleteAsync(ct);

        log.LogInformation("Ретеншн: метрики {M}, результаты {R}, инциденты {I}, уведомления {N}+{O}, команды {C}, бэкапы {B}, аудит {A}, окна {W}",
            metrics, results, incidents, notif, outbox, commands, backups, audit, windows);
    }
}
