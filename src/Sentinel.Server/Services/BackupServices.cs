using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Services;

/// <summary>Приём отчётов о бэкапах из результатов проверок и закрытие инцидентов «бэкап просрочен».</summary>
public sealed class BackupIngest(SentinelDbContext db, IncidentService incidents, ILogger<BackupIngest> log)
{
    public static bool IsBackupModule(string moduleId) => moduleId.StartsWith("backup.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Вызывается из ResultIngestService для каждого результата модуля backup.*.</summary>
    public async Task OnResultAsync(Host host, Check check, CheckResult r, CancellationToken ct)
    {
        if (r.Details is not { } details || !details.TryGetProperty("run", out var runEl)) return;
        BackupRunInfo? info;
        try { info = JsonSerializer.Deserialize<BackupRunInfo>(runEl.GetRawText(), SentinelJson.Options); }
        catch (Exception ex) { log.LogWarning(ex, "Не удалось разобрать отчёт о бэкапе для проверки {CheckId}", check.Id); return; }
        if (info is null) return;

        db.BackupRuns.Add(new BackupRun
        {
            CheckId = check.Id, HostId = host.Id, TenantId = host.TenantId, Job = info.Job, Kind = info.Kind, Target = info.Target ?? "",
            StartedAt = info.StartedAt, FinishedAt = info.FinishedAt, Success = info.Success, ArtifactPath = info.ArtifactPath, SizeBytes = info.SizeBytes,
            Sha256 = info.Sha256, Verified = info.Verified, CloudEnabled = info.CloudEnabled, CloudUploaded = info.CloudUploaded, CloudPath = info.CloudPath,
            Error = info.Error, Log = info.Log.Count == 0 ? null : string.Join("\n", info.Log), ReceivedAt = DateTimeOffset.UtcNow,
        });

        if (info.Success)
        {
            var overdue = await db.Incidents.Where(i => i.Kind == IncidentKind.BackupOverdue && i.CheckId == check.Id && i.Key == (info.Target ?? "") && i.Status != IncidentStatus.Resolved).ToListAsync(ct);
            foreach (var i in overdue) incidents.ResolveByKind(i, $"Бэкап выполнен {info.FinishedAt.ToLocalTime():dd.MM HH:mm}", info.FinishedAt);
        }
    }
}

/// <summary>
/// Dead man's switch: если по заданию нет успешного бэкапа дольше, чем полтора периода расписания (+1 ч),
/// открывается инцидент — даже если агент молчит или сломался сам сервер клиента.
/// </summary>
public sealed class BackupOverdueWatcher(IServiceScopeFactory scopes, ILogger<BackupOverdueWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Ошибка контроля просроченных бэкапов"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
        var now = DateTimeOffset.UtcNow;

        var checks = await db.Checks.Include(c => c.Host).Where(c => c.Enabled && c.ModuleId.StartsWith("backup.")).ToListAsync(ct);
        foreach (var check in checks)
        {
            BackupSchedule schedule;
            List<string> targets;
            try
            {
                var root = JsonDocument.Parse(check.SettingsJson).RootElement;
                schedule = root.TryGetProperty("schedule", out var sch) ? JsonSerializer.Deserialize<BackupSchedule>(sch.GetRawText(), SentinelJson.Options) ?? new BackupSchedule() : new BackupSchedule();
                targets = root.TryGetProperty("databases", out var dbs) && dbs.ValueKind == JsonValueKind.Array
                    ? dbs.EnumerateArray().Select(d => d.GetString() ?? "").Where(d => d.Length > 0).ToList()
                    : new List<string> { "" };
            }
            catch { continue; }

            var period = schedule.ExpectedPeriod();
            var limit = TimeSpan.FromTicks((long)(period.Ticks * 1.5)) + TimeSpan.FromHours(1);
            // Новому заданию даём период + час, прежде чем считать его просроченным.
            if (now - check.CreatedAt < period + TimeSpan.FromHours(1)) continue;

            foreach (var target in targets)
            {
                var lastSuccess = await db.BackupRuns.Where(r => r.CheckId == check.Id && r.Target == target && r.Success)
                    .OrderByDescending(r => r.FinishedAt).Select(r => (DateTimeOffset?)r.FinishedAt).FirstOrDefaultAsync(ct);
                var overdue = lastSuccess is null || now - lastSuccess.Value > limit;
                var open = await db.Incidents.AnyAsync(i => i.Kind == IncidentKind.BackupOverdue && i.CheckId == check.Id && i.Key == target && i.Status != IncidentStatus.Resolved, ct);

                if (overdue && !open)
                {
                    var summary = lastSuccess is null
                        ? $"Ни одного успешного бэкапа с момента настройки ({check.CreatedAt.ToLocalTime():dd.MM HH:mm})"
                        : $"Последний успешный бэкап {lastSuccess.Value.ToLocalTime():dd.MM HH:mm}, ожидался не реже чем раз в {Human(period)}";
                    incidents.OpenBackupOverdue(check.Host!, check, target, summary);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static string Human(TimeSpan t) => t.TotalHours < 24 ? $"{t.TotalHours:0} ч" : $"{t.TotalDays:0} дн";
}
