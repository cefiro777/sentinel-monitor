using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>Собирает AgentConfigDocument из проверок хоста.</summary>
public sealed class AgentConfigBuilder(SentinelDbContext db, SettingsProtector settings)
{
    public async Task<AgentConfigDocument?> BuildAsync(Guid agentId, CancellationToken ct)
    {
        var host = await db.Hosts.Where(h => h.Agent!.Id == agentId)
            .Include(h => h.Checks).Include(h => h.Tenant)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        if (host is null) return null;

        return new AgentConfigDocument
        {
            Version = host.ConfigVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            TenantSlug = host.Tenant?.Slug ?? "",
            HostName = host.Name,
            Checks = host.Checks.Where(c => c.Enabled).Select(c => new CheckConfig
            {
                Id = c.Id,
                ModuleId = c.ModuleId,
                Name = c.Name,
                Enabled = c.Enabled,
                IntervalSeconds = c.IntervalSeconds,
                Settings = settings.Reveal(c.SettingsJson), // секреты — только агенту, по TLS
            }).ToList(),
        };
    }
}

/// <summary>Приём результатов: антифлаппинг, обновление состояний, история, метрики, инциденты.</summary>
public sealed class ResultIngestService(SentinelDbContext db, IncidentService incidents, BackupIngest backups, ILogger<ResultIngestService> log)
{
    public async Task<int> IngestAsync(Guid agentId, IReadOnlyList<CheckResult> results, CancellationToken ct)
    {
        if (results.Count == 0) return 0;

        var host = await db.Hosts.Include(h => h.Checks).FirstOrDefaultAsync(h => h.Agent!.Id == agentId, ct);
        if (host is null) return 0;
        var checks = host.Checks.ToDictionary(c => c.Id);

        var now = DateTimeOffset.UtcNow;
        var accepted = 0;

        // Текущие состояния грузим одним запросом, чтобы не бить БД по каждому результату.
        var incomingIds = results.Select(r => r.CheckId).Distinct().ToList();
        var states = await db.CheckStates.Where(s => incomingIds.Contains(s.CheckId)).ToListAsync(ct);
        var stateMap = states.ToDictionary(s => (s.CheckId, s.Key));

        // Результаты из офлайн-буфера обрабатываем в хронологическом порядке — иначе антифлаппинг считает неверно.
        foreach (var r in results.OrderBy(r => r.At))
        {
            if (!checks.TryGetValue(r.CheckId, out var check))
            {
                log.LogDebug("Результат для неизвестной/чужой проверки {CheckId} от агента {AgentId}", r.CheckId, agentId);
                continue;
            }

            var key = r.Key ?? "";
            // Скрытый ключ: пользователь попросил его не отслеживать — ни состояния, ни инцидентов, ни метрик.
            if (check.IgnoredKeys.Contains(key)) { accepted++; continue; }
            if (!stateMap.TryGetValue((r.CheckId, key), out var state))
            {
                state = new CheckState { Id = Guid.NewGuid(), CheckId = r.CheckId, Key = key };
                db.CheckStates.Add(state);
                stateMap[(r.CheckId, key)] = state;
            }

            // Старые результаты не должны перетирать более свежее состояние.
            if (state.LastResultAt is null || r.At >= state.LastResultAt)
            {
                var first = state.LastResultAt is null;
                state.Summary = r.Summary;
                state.DetailsJson = r.Details?.GetRawText();
                state.LastResultAt = r.At;

                if (first && r.Status == state.Status)
                {
                    // Новое состояние сразу в Unknown (служба не найдена и т.п.) — это тоже инцидент.
                    state.LastChangeAt = r.At;
                    state.ProblemSince = r.At;
                    await incidents.OnCheckStateChangedAsync(host, check, state, ct);
                }
                else if (r.Status == state.Status)
                {
                    state.PendingStatus = null;
                    state.PendingCount = 0;
                    // Задержка оповещения: проблема держится — пора ли открывать инцидент (или обновить описание открытого).
                    if (state.Status != CheckStatus.Ok && check.AlertDelayMinutes > 0)
                        await incidents.OnCheckStateChangedAsync(host, check, state, ct);
                }
                else
                {
                    if (state.PendingStatus == r.Status) state.PendingCount++;
                    else { state.PendingStatus = r.Status; state.PendingCount = 1; }

                    // Бэкап — не мигающий датчик: каждый прогон окончателен, а ждать второго можно сутки.
                    var confirmNeeded = BackupIngest.IsBackupModule(check.ModuleId) ? 1 : Math.Max(1, check.ConfirmCount);

                    // Первый результат применяется сразу; дальше — только после ConfirmCount подряд.
                    if (first || state.PendingCount >= confirmNeeded)
                    {
                        state.PreviousStatus = state.Status;
                        state.Status = r.Status;
                        state.LastChangeAt = r.At;
                        // warning ↔ critical — та же непрерывная проблема, отсчёт задержки не сбрасываем.
                        if (r.Status == CheckStatus.Ok) state.ProblemSince = null;
                        else if (state.PreviousStatus == CheckStatus.Ok || first || state.ProblemSince is null) state.ProblemSince = r.At;
                        state.PendingStatus = null;
                        state.PendingCount = 0;
                        await incidents.OnCheckStateChangedAsync(host, check, state, ct);
                    }
                }
            }

            db.CheckResults.Add(new CheckResultRecord
            {
                CheckId = r.CheckId, Key = key, Status = r.Status, Summary = r.Summary, At = r.At, ReceivedAt = now,
            });
            if (BackupIngest.IsBackupModule(check.ModuleId)) await backups.OnResultAsync(host, check, r, ct);

            foreach (var m in r.Metrics)
                db.Metrics.Add(new MetricPoint { Time = r.At, CheckId = r.CheckId, Key = key, Name = m.Name, Value = m.Value });

            accepted++;
        }

        await RemoveStaleStatesAsync(checks, results, stateMap.Values, ct);
        await db.SaveChangesAsync(ct);
        return accepted;
    }

    /// <summary>
    /// Ключи, которые агент перестал присылать (сменили порт регистратора, убрали службу из списка, отвалился диск из include),
    /// не должны висеть вечно: если по проверке есть свежие результаты, а по ключу — нет уже 3 интервала, состояние удаляем,
    /// его инциденты закрываем. Бэкапы не трогаем — там результаты приходят по расписанию задания, а не по интервалу.
    /// </summary>
    private async Task RemoveStaleStatesAsync(Dictionary<Guid, Check> checks, IReadOnlyList<CheckResult> results, IEnumerable<CheckState> states, CancellationToken ct)
    {
        var freshest = results.GroupBy(r => r.CheckId).ToDictionary(g => g.Key, g => g.Max(r => r.At));
        var stale = new List<CheckState>();
        foreach (var st in states)
        {
            if (!freshest.TryGetValue(st.CheckId, out var latest) || !checks.TryGetValue(st.CheckId, out var check)) continue;
            if (BackupIngest.IsBackupModule(check.ModuleId) || st.LastResultAt is null) continue;
            var grace = TimeSpan.FromSeconds(Math.Max(check.IntervalSeconds * 3, 600));
            if (st.LastResultAt.Value < latest - grace) stale.Add(st);
        }
        if (stale.Count == 0) return;

        var ids = stale.Select(s => s.CheckId).Distinct().ToList();
        var keys = stale.Select(s => s.Key).Distinct().ToList();
        var open = await db.Incidents.Where(i => i.CheckId != null && ids.Contains(i.CheckId.Value) && keys.Contains(i.Key) && i.Kind == IncidentKind.CheckState && i.Status != IncidentStatus.Resolved).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var st in stale)
        {
            foreach (var i in open.Where(i => i.CheckId == st.CheckId && i.Key == st.Key)) incidents.ResolveSilently(i, "Ключ больше не наблюдается (изменены настройки проверки)", now);
            log.LogInformation("Удалено устаревшее состояние {Key} проверки {CheckId}: последний результат {At}", st.Key, st.CheckId, st.LastResultAt);
        }
        db.CheckStates.RemoveRange(stale);
    }
}

/// <summary>Помечает агентов офлайн, если хартбит не приходил дольше порога, и открывает инцидент.</summary>
public sealed class AgentOfflineWatcher(IServiceScopeFactory scopes, IOptions<SentinelOptions> options, ILogger<AgentOfflineWatcher> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
                var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();
                var threshold = DateTimeOffset.UtcNow.AddSeconds(-options.Value.AgentOfflineAfterSeconds);

                // Флаг IsOnline хаб сбрасывает сразу при обрыве соединения, поэтому ориентируемся на время последней связи
                // и на отсутствие уже открытого инцидента: коротких обрывов с переподключением это не касается.
                var now = DateTimeOffset.UtcNow;
                var stale = await db.Agents.Include(a => a.Host)
                    .Where(a => (a.LastSeenAt == null || a.LastSeenAt < threshold)
                             && !db.Incidents.Any(i => i.HostId == a.HostId && i.Kind == IncidentKind.AgentOffline && i.Status != IncidentStatus.Resolved)
                             // Плановая перезагрузка/работы: молчание агента в окне обслуживания — не инцидент.
                             && !db.MaintenanceWindows.Any(m => m.StartsAt <= now && m.EndsAt >= now && (m.HostId == a.HostId || (m.HostId == null && m.TenantId == a.Host!.TenantId))))
                    .ToListAsync(ct);
                foreach (var a in stale)
                {
                    a.IsOnline = false;
                    await incidents.OnAgentOfflineAsync(a.Host!, a.LastSeenAt, ct);
                }
                if (stale.Count > 0)
                {
                    await db.SaveChangesAsync(ct);
                    log.LogWarning("Агентов переведено в офлайн: {Count}", stale.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Ошибка проверки офлайн-агентов");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}

/// <summary>Выпуск команды агенту: запись в БД, подпись, отправка.</summary>
public sealed class CommandService(SentinelDbContext db, ICommandSigner signer, IAgentMessenger messenger, IOptions<SentinelOptions> options)
{
    public async Task<Command> IssueAsync(Guid agentId, string type, JsonElement? payload, Guid? userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cmd = new Command
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            Type = type,
            PayloadJson = payload?.GetRawText(),
            IssuedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.CommandTtlSeconds),
            IssuedByUserId = userId,
            Status = CommandStatus.Pending,
        };
        db.Commands.Add(cmd);
        db.AuditLog.Add(new AuditEntry
        {
            At = now, UserId = userId, AgentId = agentId, Action = "command.issue",
            TargetType = nameof(Command), TargetId = cmd.Id, DetailsJson = JsonSerializer.Serialize(new { type, payload }),
        });
        await db.SaveChangesAsync(ct);

        var envelope = new CommandEnvelope
        {
            Id = cmd.Id, AgentId = agentId, Type = type, Payload = payload, IssuedAt = cmd.IssuedAt, ExpiresAt = cmd.ExpiresAt,
        };
        signer.Sign(envelope);

        if (await messenger.SendCommandAsync(agentId, envelope, ct))
        {
            cmd.Status = CommandStatus.Sent;
            await db.SaveChangesAsync(ct);
        }
        return cmd;
    }

    /// <summary>Агент вышел на связь: отправляем всё, что накопилось в очереди и ещё не истекло.</summary>
    public async Task<int> DeliverPendingAsync(Guid agentId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await db.Commands
            .Where(c => c.AgentId == agentId && c.Status == CommandStatus.Pending && c.ExpiresAt > now)
            .OrderBy(c => c.IssuedAt).ToListAsync(ct);
        var sent = 0;
        foreach (var cmd in pending)
        {
            var envelope = new CommandEnvelope
            {
                Id = cmd.Id, AgentId = agentId, Type = cmd.Type, IssuedAt = cmd.IssuedAt, ExpiresAt = cmd.ExpiresAt,
                Payload = cmd.PayloadJson is null ? null : JsonDocument.Parse(cmd.PayloadJson).RootElement.Clone(),
            };
            signer.Sign(envelope);
            if (await messenger.SendCommandAsync(agentId, envelope, ct)) { cmd.Status = CommandStatus.Sent; sent++; }
        }
        if (sent > 0) await db.SaveChangesAsync(ct);
        return sent;
    }
}

/// <summary>Команды, не доставленные или не подтверждённые до истечения срока, помечаются как истёкшие.</summary>
public sealed class CommandExpiryWorker(IServiceScopeFactory scopes, ILogger<CommandExpiryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SentinelDbContext>();
                // Sent получает минуту сверх срока: результат может ещё идти по сети.
                var now = DateTimeOffset.UtcNow;
                var n = await db.Commands
                    .Where(c => (c.Status == CommandStatus.Pending && c.ExpiresAt < now) || (c.Status == CommandStatus.Sent && c.ExpiresAt < now.AddMinutes(-1)))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Expired)
                        .SetProperty(c => c.Error, c => c.Status == CommandStatus.Sent ? "Агент не вернул результат до истечения срока" : "Агент не вышел на связь до истечения срока")
                        .SetProperty(c => c.FinishedAt, now), ct);
                if (n > 0) log.LogInformation("Истекло команд: {Count}", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Ошибка воркера истечения команд"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
