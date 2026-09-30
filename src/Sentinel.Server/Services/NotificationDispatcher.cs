using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Contracts.Protocol;
using Sentinel.Notifications;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>Разбирает outbox: для каждого уведомления находит подходящие маршруты и отправляет в каналы.</summary>
public sealed class NotificationDispatcher(IServiceScopeFactory scopes, IEnumerable<INotificationSender> senders,
    ISecretProtector protector, IOptions<SentinelOptions> options, ILogger<NotificationDispatcher> log) : BackgroundService
{
    private readonly Dictionary<ChannelType, INotificationSender> _senders = senders.ToDictionary(s => s.Type);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessBatchAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Ошибка диспетчера уведомлений"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
        var now = DateTimeOffset.UtcNow;

        var items = await db.NotificationOutbox
            .Where(o => o.ProcessedAt == null && o.Attempts < 10)
            .OrderBy(o => o.Id).Take(50).ToListAsync(ct);
        if (items.Count == 0) return;

        var routes = await db.NotificationRoutes.Include(r => r.Channel)
            .Where(r => r.IsEnabled && r.Channel!.IsEnabled).ToListAsync(ct);

        var maintenance = await db.MaintenanceWindows.Where(m => m.StartsAt <= now && m.EndsAt >= now).ToListAsync(ct);

        foreach (var item in items)
        {
            var incident = await db.Incidents.Include(i => i.Host).ThenInclude(h => h!.Tenant).FirstOrDefaultAsync(i => i.Id == item.IncidentId, ct);
            if (incident is null) { item.ProcessedAt = now; continue; }

            // Окно обслуживания: уведомления по хосту/клиенту не шлём, но инцидент и его история остаются.
            if (item.Kind != NotificationKind.Resolved && maintenance.Any(m => m.HostId == incident.HostId || (m.HostId == null && m.TenantId == incident.TenantId)))
            {
                item.ProcessedAt = now;
                db.IncidentEvents.Add(new IncidentEvent { IncidentId = incident.Id, At = now, Type = "notified", Message = $"{item.Kind}: подавлено — окно обслуживания" });
                continue;
            }

            // Инцидент взят в работу: молчим до конца срока (о его истечении уведомит IncidentReminderWorker).
            if (item.Kind != NotificationKind.Resolved && incident.Status == IncidentStatus.InProgress && incident.SnoozedUntil > now)
            {
                item.ProcessedAt = now;
                db.IncidentEvents.Add(new IncidentEvent { IncidentId = incident.Id, At = now, Type = "notified", Message = $"{item.Kind}: подавлено — инцидент в работе до {incident.SnoozedUntil:dd.MM HH:mm}" });
                continue;
            }

            // Проблема уже ушла (например, отложили на тихие часы) — сообщать о ней поздно, а «восстановлено» не нужно.
            if (item.Kind is NotificationKind.Opened or NotificationKind.Escalated or NotificationKind.Reminder && incident.Status == IncidentStatus.Resolved)
            {
                item.ProcessedAt = now;
                db.IncidentEvents.Add(new IncidentEvent { IncidentId = incident.Id, At = now, Type = "notified", Message = $"{item.Kind}: не отправлено — к моменту отправки уже восстановлено" });
                continue;
            }

            var matching = routes.Where(r => Matches(r, incident, item.Kind)).ToList();
            var allDone = true;
            var notAnnounced = 0;
            var localHour = DateTime.Now.Hour;

            foreach (var route in matching)
            {
                if (IsQuiet(route, localHour)) { allDone = false; continue; } // отложим до конца тихих часов

                // Напоминания по определению повторяются; остальные виды — один раз на канал.
                var already = item.Kind != NotificationKind.Reminder &&
                              await db.NotificationLog.AnyAsync(l => l.IncidentId == incident.Id && l.ChannelId == route.ChannelId && l.Kind == item.Kind && l.Success, ct);
                if (already) continue;

                // «Восстановлено» — только туда, куда сообщали о проблеме. Иначе предупреждения ниже порога маршрута,
                // подавленные окном обслуживания или «в работе» приходят одними отбивками без начала.
                if (item.Kind == NotificationKind.Resolved &&
                    !await db.NotificationLog.AnyAsync(l => l.IncidentId == incident.Id && l.ChannelId == route.ChannelId && l.Success &&
                        (l.Kind == NotificationKind.Opened || l.Kind == NotificationKind.Escalated || l.Kind == NotificationKind.Reminder), ct))
                {
                    notAnnounced++;
                    continue;
                }

                var channel = route.Channel!;
                var entry = new NotificationLog { IncidentId = incident.Id, ChannelId = channel.Id, Kind = item.Kind, At = DateTimeOffset.UtcNow };
                try
                {
                    if (!_senders.TryGetValue(channel.Type, out var sender)) throw new InvalidOperationException($"Канал типа {channel.Type} не поддерживается.");
                    var settings = Encoding.UTF8.GetString(protector.Unprotect(channel.SettingsEncrypted));
                    await sender.SendAsync(settings, Format(incident, item.Kind, options.Value.PublicUrl), ct);
                    entry.Success = true;
                    channel.LastUsedAt = DateTimeOffset.UtcNow;
                    channel.LastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.Success = false;
                    entry.Error = ex.Message;
                    channel.LastError = $"{DateTimeOffset.UtcNow:dd.MM HH:mm}: {ex.Message}";
                    allDone = false;
                    log.LogWarning("Уведомление в канал «{Channel}» не доставлено: {Error}", channel.Name, ex.Message);
                }
                db.NotificationLog.Add(entry);
            }

            item.Attempts++;
            if (allDone) item.ProcessedAt = now;
            else item.LastError = "Часть каналов не доставлена или тихие часы; повтор позже";
            db.IncidentEvents.Add(new IncidentEvent { IncidentId = incident.Id, At = now, Type = "notified", Message = $"{item.Kind}: маршрутов {matching.Count}, доставлено {(allDone ? "все" : "не все")}" +
                (notAnnounced > 0 ? $", пропущено {notAnnounced} — о проблеме туда не сообщали" : "") });
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool Matches(NotificationRoute r, Incident i, NotificationKind kind)
    {
        if (r.TenantId is not null && r.TenantId != i.TenantId) return false;
        if (kind == NotificationKind.Resolved) return r.NotifyOnResolve;
        if (kind == NotificationKind.Reminder && r.RemindMinutes <= 0) return false;
        return i.Severity >= r.MinSeverity;
    }

    private static bool IsQuiet(NotificationRoute r, int hour)
    {
        if (r.QuietFromHour is not int from || r.QuietToHour is not int to || from == to) return false;
        return from < to ? hour >= from && hour < to : hour >= from || hour < to; // 23–7 через полночь
    }

    public static NotificationMessage Format(Incident i, NotificationKind kind, string publicUrl)
    {
        var tenant = i.Host?.Tenant?.Name ?? "";
        var link = i.Id == Guid.Empty ? publicUrl : $"{publicUrl.TrimEnd('/')}/incidents/{i.Id}";
        var (emoji, word) = kind switch
        {
            NotificationKind.Resolved => ("✅", "Восстановлено"),
            NotificationKind.Escalated => ("⬆️", "Ухудшение"),
            NotificationKind.Reminder => ("🔁", $"НАПОМИНАНИЕ #{i.ReminderCount}: инцидент не взят в работу"),
            NotificationKind.Test => ("🔔", "Тест"),
            _ => i.Severity == CheckStatus.Critical ? ("🔴", "КРИТИЧНО") : i.Severity == CheckStatus.Unknown ? ("⚪", "НЕИЗВЕСТНО") : ("🟡", "Внимание"),
        };
        var duration = kind == NotificationKind.Resolved && i.ResolvedAt is not null ? Human(i.ResolvedAt.Value - i.OpenedAt) : null;

        var subject = $"[Sentinel] {word}: {i.Title}" + (tenant.Length > 0 ? $" ({tenant})" : "");
        var plain = new StringBuilder()
            .AppendLine($"{word} · {tenant}")
            .AppendLine(i.Title)
            .AppendLine(i.Summary)
            .AppendLine(duration is null ? $"Открыт: {i.OpenedAt.ToLocalTime():dd.MM.yyyy HH:mm}" : $"Длительность: {duration}")
            .AppendLine(link).ToString();

        var tg = $"{emoji} <b>{word}</b> · {E(tenant)}\n<b>{E(i.Title)}</b>\n{E(i.Summary)}\n" +
                 (duration is null ? $"<i>Открыт {i.OpenedAt.ToLocalTime():dd.MM HH:mm}</i>" : $"<i>Длительность {E(duration)}</i>") +
                 $"\n<a href=\"{link}\">Открыть в Sentinel</a>";

        var color = kind == NotificationKind.Resolved ? "#22c55e" : i.Severity == CheckStatus.Critical ? "#ef4444" : "#f59e0b";
        var html = $@"<div style=""font-family:system-ui,sans-serif;font-size:14px"">
<div style=""border-left:4px solid {color};padding:8px 12px"">
<div style=""color:#666"">{emoji} {E(word)} · {E(tenant)}</div>
<div style=""font-size:16px;font-weight:600;margin:4px 0"">{E(i.Title)}</div>
<div>{E(i.Summary)}</div>
<div style=""color:#666;margin-top:6px"">{(duration is null ? $"Открыт {i.OpenedAt.ToLocalTime():dd.MM.yyyy HH:mm}" : $"Длительность {E(duration)}")}</div>
<div style=""margin-top:8px""><a href=""{link}"">Открыть в Sentinel</a></div>
</div></div>";
        return new NotificationMessage(subject, plain, html, tg);
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Human(TimeSpan t) => t.TotalMinutes < 1 ? "меньше минуты" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} мин" : t.TotalDays < 1 ? $"{t.Hours} ч {t.Minutes} мин" : $"{(int)t.TotalDays} д {t.Hours} ч";
}
