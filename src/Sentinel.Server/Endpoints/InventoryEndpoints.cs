using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Protocol;
using Sentinel.Contracts.Security;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Endpoints;

/// <summary>Клиенты, хосты, проверки, токены регистрации, команды — то, чем управляет дашборд.</summary>
public static class InventoryEndpoints
{
    public sealed record TenantDto(Guid Id, string Name, string Slug, string? Notes, bool IsActive, int HostCount, int AgentsOnline, CheckStatus WorstStatus);
    public sealed record TenantUpsert(string Name, string Slug, string? Notes);
    public sealed record TenantDelete(string? Confirm);

    public sealed record HostDto(Guid Id, Guid TenantId, string TenantName, string Name, HostKind Kind, string? Address, string? Description,
        AgentDto? Agent, CheckStatus WorstStatus, int ChecksTotal, int ChecksProblem, int ChecksCritical, int ChecksWarning, int ChecksInProgress);
    public sealed record AgentDto(Guid Id, bool IsOnline, DateTimeOffset? LastSeenAt, string AgentVersion, string OsVersion, int ReportedConfigVersion, int ConfigVersion, int QueueDepth);
    public sealed record HostUpsert(Guid TenantId, string Name, HostKind Kind, string? Address, string? Description);

    public sealed record CheckDto(Guid Id, string ModuleId, string Name, bool Enabled, int IntervalSeconds, int ConfirmCount, JsonElement Settings, List<CheckStateDto> States, List<string> IgnoredKeys, int AlertDelayMinutes);
    public sealed record CheckStateDto(string Key, CheckStatus Status, string Summary, JsonElement? Details, DateTimeOffset? LastResultAt, DateTimeOffset? LastChangeAt,
        CheckStatus? PendingStatus, int PendingCount, int ConfirmCount, DateTimeOffset? ProblemSince = null);
    public sealed record CheckUpsert(string ModuleId, string Name, bool Enabled, int IntervalSeconds, int? ConfirmCount, JsonElement Settings, List<string>? IgnoredKeys = null, int? AlertDelayMinutes = null);

    public sealed record EnrollmentTokenDto(Guid Id, Guid TenantId, Guid? HostId, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, string? Comment);
    public sealed record CreateEnrollmentToken(Guid TenantId, Guid? HostId, Guid? SiteId, int? TtlHours, string? Comment);
    public sealed record EnrollmentTokenCreated(Guid Id, string Token, DateTimeOffset ExpiresAt, string InstallHint, string ServerUrl, string? CertificatePin);

    public sealed record CommandDto(Guid Id, string Type, CommandStatus Status, DateTimeOffset IssuedAt, DateTimeOffset? FinishedAt, string? Output, string? Error);
    public sealed record IssueCommand(string Type, JsonElement? Payload);

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var tenants = app.MapGroup("/api/tenants").WithTags("Tenants").RequireAuthorization(UserPolicy);
        tenants.MapGet("/", ListTenants);
        tenants.MapPost("/", CreateTenant).RequireAuthorization(OperatorPolicy);
        tenants.MapPut("/{id:guid}", UpdateTenant).RequireAuthorization(OperatorPolicy);
        tenants.MapDelete("/{id:guid}", DeleteTenant).RequireAuthorization(AdminPolicy);

        var hosts = app.MapGroup("/api/hosts").WithTags("Hosts").RequireAuthorization(UserPolicy);
        hosts.MapGet("/", ListHosts);
        hosts.MapGet("/{id:guid}", GetHost);
        hosts.MapPost("/", CreateHost).RequireAuthorization(OperatorPolicy);
        hosts.MapDelete("/{id:guid}", DeleteHost).RequireAuthorization(OperatorPolicy);
        hosts.MapGet("/{id:guid}/checks", ListChecks);
        hosts.MapPost("/{id:guid}/checks", CreateCheck).RequireAuthorization(OperatorPolicy);
        hosts.MapPut("/{id:guid}/checks/{checkId:guid}", UpdateCheck).RequireAuthorization(OperatorPolicy);
        hosts.MapDelete("/{id:guid}/checks/{checkId:guid}", DeleteCheck).RequireAuthorization(OperatorPolicy);
        hosts.MapGet("/{id:guid}/commands", ListCommands);
        hosts.MapGet("/{id:guid}/metrics", ListMetrics);
        hosts.MapPost("/{id:guid}/commands", IssueHostCommand).RequireAuthorization(MfaOperatorPolicy);

        var tokens = app.MapGroup("/api/enrollment-tokens").WithTags("Enrollment").RequireAuthorization(OperatorPolicy);
        tokens.MapGet("/", ListTokens);
        tokens.MapPost("/", CreateToken);
        tokens.MapDelete("/{id:guid}", RevokeToken);

        return app;
    }

    // ---- Tenants ----

    private static async Task<IResult> ListTenants(HttpContext http, SentinelDbContext db)
    {
        var scope = http.User.GetTenantScope();
        var q = db.Tenants.AsNoTracking().Where(t => scope == null || t.Id == scope);
        var list = await q.OrderBy(t => t.Name).Select(t => new TenantDto(
            t.Id, t.Name, t.Slug, t.Notes, t.IsActive,
            t.Hosts.Count,
            t.Hosts.Count(h => h.Agent != null && h.Agent.IsOnline),
            t.Hosts.SelectMany(h => h.Checks).SelectMany(c => c.States).Max(s => (CheckStatus?)s.Status) ?? CheckStatus.Ok
        )).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateTenant(TenantUpsert req, SentinelDbContext db, HttpContext http)
    {
        var slug = NormalizeSlug(req.Slug, req.Name);
        if (await db.Tenants.AnyAsync(t => t.Slug == slug)) return Results.Conflict(new { error = "Slug уже занят." });
        var t = new Tenant { Id = Guid.NewGuid(), Name = req.Name.Trim(), Slug = slug, Notes = req.Notes, CreatedAt = DateTimeOffset.UtcNow };
        db.Tenants.Add(t);
        Audit(db, http, "tenant.create", nameof(Tenant), t.Id, t.Id);
        await db.SaveChangesAsync();
        return Results.Created($"/api/tenants/{t.Id}", new TenantDto(t.Id, t.Name, t.Slug, t.Notes, t.IsActive, 0, 0, CheckStatus.Ok));
    }

    private static async Task<IResult> UpdateTenant(Guid id, TenantUpsert req, SentinelDbContext db, HttpContext http)
    {
        var t = await db.Tenants.FindAsync(id);
        if (t is null) return Results.NotFound();
        t.Name = req.Name.Trim();
        t.Slug = NormalizeSlug(req.Slug, req.Name);
        t.Notes = req.Notes;
        Audit(db, http, "tenant.update", nameof(Tenant), t.Id, t.Id);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// Удаление клиента вместе с хостами, проверками, историей и инцидентами (каскад в БД).
    /// Требуется точное имя клиента в подтверждении — защита от случайного удаления не того клиента.
    /// </summary>
    private static async Task<IResult> DeleteTenant(Guid id, [FromBody] TenantDelete req, SentinelDbContext db, HttpContext http)
    {
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Results.NotFound();
        if (!string.Equals(req.Confirm?.Trim(), t.Name, StringComparison.Ordinal))
            return Results.BadRequest(new { error = $"Для удаления введите имя клиента точно: «{t.Name}»." });

        var hosts = await db.Hosts.CountAsync(h => h.TenantId == id);
        db.Tenants.Remove(t);
        db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = null, Action = "tenant.delete",
            TargetType = nameof(Tenant), TargetId = t.Id, DetailsJson = JsonSerializer.Serialize(new { name = t.Name, slug = t.Slug, hosts }),
            RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
        return Results.Ok(new { deletedHosts = hosts });
    }

    // ---- Hosts ----

    // Фильтры применяются до проекции в DTO: после Select EF не может транслировать условия по полям записи.
    private static IQueryable<HostDto> HostQuery(SentinelDbContext db, Guid? tenantScope, Guid? hostId = null) =>
        db.Hosts.AsNoTracking()
            .Where(h => tenantScope == null || h.TenantId == tenantScope)
            .Where(h => hostId == null || h.Id == hostId)
            .OrderBy(h => h.Tenant!.Name).ThenBy(h => h.Name)
            .Select(h => new HostDto(
                h.Id, h.TenantId, h.Tenant!.Name, h.Name, h.Kind, h.Address, h.Description,
                h.Agent == null ? null : new AgentDto(h.Agent.Id, h.Agent.IsOnline, h.Agent.LastSeenAt, h.Agent.AgentVersion, h.Agent.OsVersion,
                    h.Agent.ReportedConfigVersion, h.ConfigVersion, h.Agent.ReportedQueueDepth),
                h.Checks.SelectMany(c => c.States).Max(s => (CheckStatus?)s.Status) ?? CheckStatus.Ok,
                h.Checks.Count,
                h.Checks.SelectMany(c => c.States).Count(s => s.Status != CheckStatus.Ok),
                h.Checks.SelectMany(c => c.States).Count(s => s.Status == CheckStatus.Critical),
                h.Checks.SelectMany(c => c.States).Count(s => s.Status == CheckStatus.Warning),
                // Проблемы, по которым инженер уже работает: не должны шуметь в сводках.
                db.Incidents.Count(i => i.HostId == h.Id && i.Status == IncidentStatus.InProgress)));

    private static async Task<IResult> ListHosts(HttpContext http, SentinelDbContext db, Guid? tenantId)
    {
        var scope = http.User.GetTenantScope() ?? tenantId;
        return Results.Ok(await HostQuery(db, scope).ToListAsync());
    }

    private static async Task<IResult> GetHost(Guid id, HttpContext http, SentinelDbContext db)
    {
        var host = await HostQuery(db, http.User.GetTenantScope(), id).FirstOrDefaultAsync();
        return host is null ? Results.NotFound() : Results.Ok(host);
    }

    private static async Task<IResult> CreateHost(HostUpsert req, SentinelDbContext db, HttpContext http)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId)) return Results.BadRequest(new { error = "Клиент не найден." });
        var h = new Host
        {
            Id = Guid.NewGuid(), TenantId = req.TenantId, Name = req.Name.Trim(), Kind = req.Kind,
            Address = req.Address, Description = req.Description, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Hosts.Add(h);
        Audit(db, http, "host.create", nameof(Host), h.Id, h.TenantId);
        await db.SaveChangesAsync();
        return Results.Created($"/api/hosts/{h.Id}", await HostQuery(db, null, h.Id).FirstAsync());
    }

    private static async Task<IResult> DeleteHost(Guid id, SentinelDbContext db, HttpContext http)
    {
        var h = await db.Hosts.FindAsync(id);
        if (h is null) return Results.NotFound();
        db.Hosts.Remove(h);
        Audit(db, http, "host.delete", nameof(Host), h.Id, h.TenantId);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- Checks ----

    private static async Task<IResult> ListChecks(Guid id, HttpContext http, SentinelDbContext db, SettingsProtector sp)
    {
        var scope = http.User.GetTenantScope();
        var host = await db.Hosts.AsNoTracking().Include(h => h.Checks).ThenInclude(c => c.States)
            .FirstOrDefaultAsync(h => h.Id == id && (scope == null || h.TenantId == scope));
        if (host is null) return Results.NotFound();
        return Results.Ok(host.Checks.OrderBy(c => c.Name).Select(c => ToDto(c, sp)).ToList());
    }

    private static async Task<IResult> CreateCheck(Guid id, CheckUpsert req, SentinelDbContext db, HttpContext http, IAgentMessenger messenger, SettingsProtector sp)
    {
        var host = await db.Hosts.Include(h => h.Agent).FirstOrDefaultAsync(h => h.Id == id);
        if (host is null) return Results.NotFound();
        if (req.IntervalSeconds < 10) return Results.BadRequest(new { error = "Интервал не меньше 10 секунд." });

        var now = DateTimeOffset.UtcNow;
        var c = new Check
        {
            Id = Guid.NewGuid(), HostId = host.Id, ModuleId = req.ModuleId.Trim(), Name = req.Name.Trim(), Enabled = req.Enabled,
            IntervalSeconds = req.IntervalSeconds, ConfirmCount = Math.Clamp(req.ConfirmCount ?? 2, 1, 10),
            SettingsJson = sp.ProtectForStorage(req.Settings, null), CreatedAt = now, UpdatedAt = now,
            IgnoredKeys = NormalizeKeys(req.IgnoredKeys),
            AlertDelayMinutes = ClampDelay(req.AlertDelayMinutes ?? DefaultAlertDelay(req.ModuleId)),
        };
        db.Checks.Add(c);
        await BumpConfigAsync(host, db, http, messenger, "check.create", c.Id);
        return Results.Created($"/api/hosts/{id}/checks/{c.Id}", ToDto(c, sp));
    }

    /// <summary>Пики CPU/памяти кратковременны и нормальны — у «Системы» по умолчанию оповещение после 30 мин.</summary>
    internal static int DefaultAlertDelay(string moduleId) => moduleId.Trim() == "system" ? 30 : 0;
    internal static int ClampDelay(int minutes) => Math.Clamp(minutes, 0, 24 * 60);

    private static List<string> NormalizeKeys(List<string>? keys)
        => (keys ?? new()).Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private static async Task<IResult> UpdateCheck(Guid id, Guid checkId, CheckUpsert req, SentinelDbContext db, HttpContext http, IAgentMessenger messenger, SettingsProtector sp, IncidentService incidents)
    {
        var host = await db.Hosts.Include(h => h.Agent).FirstOrDefaultAsync(h => h.Id == id);
        var c = await db.Checks.Include(x => x.States).FirstOrDefaultAsync(x => x.Id == checkId && x.HostId == id);
        if (host is null || c is null) return Results.NotFound();
        if (req.IntervalSeconds < 10) return Results.BadRequest(new { error = "Интервал не меньше 10 секунд." });

        c.ModuleId = req.ModuleId.Trim();
        c.Name = req.Name.Trim();
        c.Enabled = req.Enabled;
        c.IntervalSeconds = req.IntervalSeconds;
        c.ConfirmCount = Math.Clamp(req.ConfirmCount ?? c.ConfirmCount, 1, 10);
        c.AlertDelayMinutes = ClampDelay(req.AlertDelayMinutes ?? c.AlertDelayMinutes);
        // Состояния не сбрасываем: ключи, которые агент перестанет присылать, ResultIngestService уберёт сам (тихо),
        // а остальные сохранят статус — без ложных «восстановлено» и пустой страницы на минуту.
        c.SettingsJson = sp.ProtectForStorage(req.Settings, c.SettingsJson);
        c.UpdatedAt = DateTimeOffset.UtcNow;
        if (req.IgnoredKeys is not null)
        {
            var ignored = NormalizeKeys(req.IgnoredKeys);
            var newlyHidden = ignored.Except(c.IgnoredKeys).ToList();
            c.IgnoredKeys = ignored;
            if (newlyHidden.Count > 0)
            {
                // Скрытый ключ исчезает сразу: состояние удаляем, его инциденты закрываем без уведомления.
                db.CheckStates.RemoveRange(c.States.Where(st => newlyHidden.Contains(st.Key)));
                var open = await db.Incidents.Where(i => i.CheckId == c.Id && newlyHidden.Contains(i.Key) && i.Status != IncidentStatus.Resolved).ToListAsync();
                foreach (var i in open) incidents.ResolveSilently(i, "Показатель скрыт — больше не отслеживается", DateTimeOffset.UtcNow);
            }
        }
        await BumpConfigAsync(host, db, http, messenger, "check.update", c.Id);
        return Results.Ok(ToDto(c, sp));
    }

    private static async Task<IResult> DeleteCheck(Guid id, Guid checkId, SentinelDbContext db, HttpContext http, IAgentMessenger messenger, IncidentService incidents)
    {
        var host = await db.Hosts.Include(h => h.Agent).FirstOrDefaultAsync(h => h.Id == id);
        var c = await db.Checks.FirstOrDefaultAsync(x => x.Id == checkId && x.HostId == id);
        if (host is null || c is null) return Results.NotFound();

        // Инциденты удаляемой проверки иначе остались бы открытыми навсегда — закрываем тихо, это не «восстановление».
        var open = await db.Incidents.Where(i => i.CheckId == c.Id && i.Status != IncidentStatus.Resolved).ToListAsync();
        foreach (var i in open) incidents.ResolveSilently(i, "Проверка удалена", DateTimeOffset.UtcNow);

        db.Checks.Remove(c);
        await BumpConfigAsync(host, db, http, messenger, "check.delete", c.Id);
        return Results.NoContent();
    }

    private static async Task BumpConfigAsync(Host host, SentinelDbContext db, HttpContext http, IAgentMessenger messenger, string action, Guid checkId)
    {
        host.ConfigVersion++;
        Audit(db, http, action, nameof(Check), checkId, host.TenantId);
        await db.SaveChangesAsync();
        if (host.Agent is not null) await messenger.NotifyConfigChangedAsync(host.Agent.Id, host.ConfigVersion);
    }

    private static CheckDto ToDto(Check c, SettingsProtector sp) => new(c.Id, c.ModuleId, c.Name, c.Enabled, c.IntervalSeconds, c.ConfirmCount,
        sp.MaskSecrets(c.SettingsJson),
        c.States.OrderBy(s => s.Key).Select(s => new CheckStateDto(s.Key, s.Status, s.Summary,
            s.DetailsJson is null ? null : JsonDocument.Parse(s.DetailsJson).RootElement.Clone(), s.LastResultAt, s.LastChangeAt,
            s.PendingStatus, s.PendingCount, c.ConfirmCount, s.ProblemSince)).ToList(),
        c.IgnoredKeys.OrderBy(k => k).ToList(), c.AlertDelayMinutes);

    // ---- Metrics ----

    public sealed record MetricSeriesDto(Guid CheckId, string Key, string Name, List<double[]> Points);

    /// <summary>
    /// Ряды метрик хоста за последние часы для мини-графиков: точки сведены в корзины (среднее), не больше ~48 на ряд.
    /// Point = [unix-время в мс, значение].
    /// </summary>
    private static async Task<IResult> ListMetrics(Guid id, HttpContext http, SentinelDbContext db, int? hours)
    {
        var scope = http.User.GetTenantScope();
        var host = await db.Hosts.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && (scope == null || h.TenantId == scope));
        if (host is null) return Results.NotFound();

        var span = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 24 * 14));
        var since = DateTimeOffset.UtcNow - span;
        var bucket = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, span.Ticks / 48));
        var checkIds = await db.Checks.Where(c => c.HostId == id).Select(c => c.Id).ToListAsync();
        var rows = await db.Metrics.AsNoTracking()
            .Where(m => checkIds.Contains(m.CheckId) && m.Time >= since)
            .Select(m => new { m.CheckId, m.Key, m.Name, m.Time, m.Value })
            .ToListAsync();

        var series = rows
            .GroupBy(r => (r.CheckId, r.Key, r.Name))
            .Select(g => new MetricSeriesDto(g.Key.CheckId, g.Key.Key, g.Key.Name,
                g.GroupBy(r => r.Time.UtcTicks / bucket.Ticks)
                 .OrderBy(b => b.Key)
                 .Select(b => new[] { (double)new DateTimeOffset(b.Key * bucket.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds(), Math.Round(b.Average(r => r.Value), 2) })
                 .ToList()))
            .ToList();
        return Results.Ok(series);
    }

    // ---- Commands ----

    private static async Task<IResult> ListCommands(Guid id, HttpContext http, SentinelDbContext db)
    {
        var scope = http.User.GetTenantScope();
        var agentId = await db.Hosts.Where(h => h.Id == id && (scope == null || h.TenantId == scope)).Select(h => h.Agent!.Id).FirstOrDefaultAsync();
        if (agentId == Guid.Empty) return Results.NotFound();
        var list = await db.Commands.AsNoTracking().Where(c => c.AgentId == agentId).OrderByDescending(c => c.IssuedAt).Take(50)
            .Select(c => new CommandDto(c.Id, c.Type, c.Status, c.IssuedAt, c.FinishedAt, c.Output, c.Error)).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> IssueHostCommand(Guid id, IssueCommand req, HttpContext http, SentinelDbContext db, CommandService commands, CancellationToken ct)
    {
        var agentId = await db.Hosts.Where(h => h.Id == id).Select(h => h.Agent!.Id).FirstOrDefaultAsync(ct);
        if (agentId == Guid.Empty) return Results.BadRequest(new { error = "На хосте нет агента." });
        var cmd = await commands.IssueAsync(agentId, req.Type, req.Payload, http.User.GetUserId(), ct);
        return Results.Accepted($"/api/hosts/{id}/commands", new CommandDto(cmd.Id, cmd.Type, cmd.Status, cmd.IssuedAt, cmd.FinishedAt, cmd.Output, cmd.Error));
    }

    // ---- Enrollment tokens ----

    private static async Task<IResult> ListTokens(SentinelDbContext db, Guid? tenantId)
    {
        var list = await db.EnrollmentTokens.AsNoTracking()
            .Where(t => tenantId == null || t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt).Take(100)
            .Select(t => new EnrollmentTokenDto(t.Id, t.TenantId, t.HostId, t.ExpiresAt, t.UsedAt, t.Comment)).ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> CreateToken(CreateEnrollmentToken req, SentinelDbContext db, HttpContext http,
        Microsoft.Extensions.Options.IOptions<Options.SentinelOptions> options, CertificatePinProvider pins)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId)) return Results.BadRequest(new { error = "Клиент не найден." });

        // 32 случайных байта → base64url; в БД только хеш.
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var t = new EnrollmentToken
        {
            Id = Guid.NewGuid(), TenantId = req.TenantId, HostId = req.HostId, SiteId = req.SiteId,
            TokenHash = AgentAuth.Sha256Hex(Encoding.UTF8.GetBytes(raw)),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(Math.Clamp(req.TtlHours ?? 24, 1, 24 * 14)),
            CreatedByUserId = http.User.GetUserId(), Comment = req.Comment,
        };
        db.EnrollmentTokens.Add(t);
        Audit(db, http, "enrollment.create", nameof(EnrollmentToken), t.Id, t.TenantId);
        await db.SaveChangesAsync();

        var pin = pins.Get();
        var hint = $"SentinelAgent.exe enroll --server {options.Value.PublicUrl} --token {raw}" + (pin is null ? "" : $" --pin {pin}");
        return Results.Created($"/api/enrollment-tokens/{t.Id}", new EnrollmentTokenCreated(t.Id, raw, t.ExpiresAt, hint, options.Value.PublicUrl.TrimEnd('/'), pin));
    }

    private static async Task<IResult> RevokeToken(Guid id, SentinelDbContext db, HttpContext http)
    {
        var t = await db.EnrollmentTokens.FindAsync(id);
        if (t is null) return Results.NotFound();
        if (t.UsedAt is null) t.ExpiresAt = DateTimeOffset.UtcNow;
        Audit(db, http, "enrollment.revoke", nameof(EnrollmentToken), t.Id, t.TenantId);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    // ---- helpers ----

    private static void Audit(SentinelDbContext db, HttpContext http, string action, string targetType, Guid targetId, Guid? tenantId)
        => db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = tenantId, Action = action,
            TargetType = targetType, TargetId = targetId, RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });

    private static string NormalizeSlug(string? slug, string name)
    {
        var src = string.IsNullOrWhiteSpace(slug) ? name : slug;
        var sb = new StringBuilder();
        foreach (var ch in src.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : s;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
