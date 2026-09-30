using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Services;

/// <summary>
/// Жизненный цикл инцидентов. Работает в рамках DbContext вызывающего — сохраняет вызывающий.
/// Каждое открытие/закрытие кладёт запись в outbox уведомлений.
/// </summary>
public sealed class IncidentService(SentinelDbContext db, ILogger<IncidentService> log)
{
    /// <summary>Подтверждённая смена состояния проверки.</summary>
    public async Task OnCheckStateChangedAsync(Host host, Check check, CheckState state, CancellationToken ct)
    {
        var open = await db.Incidents.FirstOrDefaultAsync(i =>
            i.Kind == IncidentKind.CheckState && i.CheckId == check.Id && i.Key == state.Key && i.Status != IncidentStatus.Resolved, ct);

        if (state.Status == CheckStatus.Ok)
        {
            if (open is not null) Resolve(open, state.Summary, state.LastResultAt ?? DateTimeOffset.UtcNow);
            return;
        }

        if (open is null)
        {
            var at = state.LastResultAt ?? DateTimeOffset.UtcNow;
            var summary = state.Summary;
            if (check.AlertDelayMinutes > 0)
            {
                // Кратковременный пик — норма: ждём, пока проблема продержится дольше задержки. Инцидент — с её начала.
                var since = state.ProblemSince ?? state.LastChangeAt ?? at;
                var held = at - since;
                if (held < TimeSpan.FromMinutes(check.AlertDelayMinutes)) return;
                summary += $" · держится {Math.Round(held.TotalMinutes):0} мин";
                at = since;
            }
            Open(host, check.Id, state.Key, IncidentKind.CheckState, state.Status,
                $"{host.Name} — {check.Name}{(state.Key.Length > 0 ? " / " + state.Key : "")}", summary, at);
            return;
        }

        // Инцидент уже есть: обновляем описание; при ухудшении — событие и повторное уведомление.
        var now = DateTimeOffset.UtcNow;
        open.Summary = state.Summary;
        open.UpdatedAt = now;
        if (state.Status > open.Severity)
        {
            db.IncidentEvents.Add(new IncidentEvent { IncidentId = open.Id, At = now, Type = "severity", Message = $"{open.Severity} → {state.Status}: {state.Summary}" });
            open.Severity = state.Status;
            // Инцидент в работе: инженер знает о проблеме — не шумим до конца срока.
            if (open.Status != IncidentStatus.InProgress)
                db.NotificationOutbox.Add(new NotificationOutbox { IncidentId = open.Id, Kind = NotificationKind.Escalated, CreatedAt = now });
        }
        else if (state.Status < open.Severity)
        {
            db.IncidentEvents.Add(new IncidentEvent { IncidentId = open.Id, At = now, Type = "severity", Message = $"{open.Severity} → {state.Status}: {state.Summary}" });
            open.Severity = state.Status;
        }
    }

    public async Task OnAgentOfflineAsync(Host host, DateTimeOffset? lastSeen, CancellationToken ct)
    {
        var exists = await db.Incidents.AnyAsync(i => i.Kind == IncidentKind.AgentOffline && i.HostId == host.Id && i.Status != IncidentStatus.Resolved, ct);
        if (exists) return;
        Open(host, null, "", IncidentKind.AgentOffline, CheckStatus.Critical, $"{host.Name} — агент не на связи",
            lastSeen is null ? "Агент ни разу не выходил на связь" : $"Последняя связь {lastSeen:dd.MM HH:mm} UTC", DateTimeOffset.UtcNow);
    }

    public async Task OnAgentOnlineAsync(Guid hostId, CancellationToken ct)
    {
        var open = await db.Incidents.Where(i => i.Kind == IncidentKind.AgentOffline && i.HostId == hostId && i.Status != IncidentStatus.Resolved).ToListAsync(ct);
        foreach (var i in open) Resolve(i, "Агент снова на связи", DateTimeOffset.UtcNow);
    }

    public void OpenBackupOverdue(Host host, Check check, string target, string summary)
        => Open(host, check.Id, target, IncidentKind.BackupOverdue, CheckStatus.Critical,
            $"{host.Name} — бэкап просрочен: {check.Name}{(target.Length > 0 ? " / " + target : "")}", summary, DateTimeOffset.UtcNow);

    /// <summary>Закрытие инцидента системного вида (бэкап пришёл, агент вернулся) — без пользователя.</summary>
    public void ResolveByKind(Incident i, string message, DateTimeOffset at) => Resolve(i, message, at);

    /// <summary>Закрытие без уведомления: ключ перестал наблюдаться из-за перенастройки — это не «восстановление».</summary>
    public void ResolveSilently(Incident i, string message, DateTimeOffset at) => Resolve(i, message, at, notify: false);

    public void Acknowledge(Incident i, Guid userId, string? comment)
    {
        var now = DateTimeOffset.UtcNow;
        if (i.Status == IncidentStatus.Open)
        {
            i.Status = IncidentStatus.Acknowledged;
            i.AcknowledgedAt = now;
            i.AcknowledgedByUserId = userId;
            i.UpdatedAt = now;
            db.IncidentEvents.Add(new IncidentEvent { IncidentId = i.Id, At = now, Type = "acknowledged", Message = comment ?? "", UserId = userId });
        }
        else if (!string.IsNullOrWhiteSpace(comment))
        {
            db.IncidentEvents.Add(new IncidentEvent { IncidentId = i.Id, At = now, Type = "comment", Message = comment, UserId = userId });
        }
    }

    /// <summary>Взять в работу до срока: уведомления по инциденту молчат, пока срок не вышел.</summary>
    public void TakeInProgress(Incident i, Guid userId, DateTimeOffset until, string? comment)
    {
        var now = DateTimeOffset.UtcNow;
        i.Status = IncidentStatus.InProgress;
        i.SnoozedUntil = until;
        i.InProgressByUserId = userId;
        i.AcknowledgedAt ??= now;
        i.AcknowledgedByUserId ??= userId;
        i.UpdatedAt = now;
        // Напоминания начинаем считать заново — после срока они пойдут с чистого листа.
        i.LastReminderAt = null;
        i.ReminderCount = 0;
        db.IncidentEvents.Add(new IncidentEvent
        {
            IncidentId = i.Id, At = now, Type = "in_progress", UserId = userId,
            Message = $"взят в работу до {until.ToLocalTime():dd.MM HH:mm}" + (string.IsNullOrWhiteSpace(comment) ? "" : ": " + comment),
        });
    }

    /// <summary>Срок работы истёк, а проблема осталась: инцидент снова открыт и уведомляет.</summary>
    public void ExpireInProgress(Incident i, DateTimeOffset now)
    {
        i.Status = IncidentStatus.Open;
        i.SnoozedUntil = null;
        i.UpdatedAt = now;
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = i.Id, At = now, Type = "in_progress_expired", Message = "срок работы истёк, проблема не устранена" });
        db.NotificationOutbox.Add(new NotificationOutbox { IncidentId = i.Id, Kind = NotificationKind.Escalated, CreatedAt = now });
        log.LogWarning("Инцидент снова открыт (истёк срок «в работе»): {Title}", i.Title);
    }

    public void ResolveManually(Incident i, Guid userId, string? comment)
    {
        if (i.Status == IncidentStatus.Resolved) return;
        Resolve(i, "Закрыт вручную" + (string.IsNullOrWhiteSpace(comment) ? "" : ": " + comment), DateTimeOffset.UtcNow, userId);
    }

    private void Open(Host host, Guid? checkId, string key, IncidentKind kind, CheckStatus severity, string title, string summary, DateTimeOffset at)
    {
        var i = new Incident
        {
            Id = Guid.NewGuid(), TenantId = host.TenantId, HostId = host.Id, CheckId = checkId, Key = key, Kind = kind,
            Severity = severity, Status = IncidentStatus.Open, Title = title, Summary = summary, OpenedAt = at, UpdatedAt = at,
        };
        db.Incidents.Add(i);
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = i.Id, At = at, Type = "opened", Message = summary });
        db.NotificationOutbox.Add(new NotificationOutbox { IncidentId = i.Id, Kind = NotificationKind.Opened, CreatedAt = DateTimeOffset.UtcNow });
        log.LogWarning("Инцидент [{Severity}] {Title}: {Summary}", severity, title, summary);
    }

    private void Resolve(Incident i, string message, DateTimeOffset at, Guid? userId = null, bool notify = true)
    {
        i.Status = IncidentStatus.Resolved;
        i.SnoozedUntil = null;
        i.ResolvedAt = at;
        i.UpdatedAt = at;
        i.Summary = message;
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = i.Id, At = at, Type = "resolved", Message = message, UserId = userId });
        if (notify) db.NotificationOutbox.Add(new NotificationOutbox { IncidentId = i.Id, Kind = NotificationKind.Resolved, CreatedAt = DateTimeOffset.UtcNow });
        log.LogInformation("Инцидент закрыт: {Title} — {Message}", i.Title, message);
    }
}
