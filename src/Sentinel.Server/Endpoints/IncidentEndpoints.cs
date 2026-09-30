using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;

namespace Sentinel.Server.Endpoints;

public static class IncidentEndpoints
{
    public sealed record IncidentDto(Guid Id, Guid TenantId, string TenantName, Guid HostId, string HostName, Guid? CheckId, string Key,
        IncidentKind Kind, CheckStatus Severity, IncidentStatus Status, string Title, string Summary,
        DateTimeOffset OpenedAt, DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SnoozedUntil);
    public sealed record IncidentEventDto(DateTimeOffset At, string Type, string Message, string? UserName);
    public sealed record IncidentDetailDto(IncidentDto Incident, List<IncidentEventDto> Events);
    public sealed record IncidentAction(string? Comment);
    /// <summary>Взять в работу на Hours часов (или до Until).</summary>
    public sealed record TakeInProgressRequest(int? Hours, DateTimeOffset? Until, string? Comment);
    public sealed record IncidentSummaryDto(int Open, int Acknowledged, int Critical, int ResolvedToday);

    public static IEndpointRouteBuilder MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/incidents").WithTags("Incidents").RequireAuthorization(UserPolicy);
        g.MapGet("/", List);
        g.MapGet("/summary", Summary);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/{id:guid}/ack", Ack).RequireAuthorization(OperatorPolicy);
        g.MapPost("/{id:guid}/in-progress", TakeInProgress).RequireAuthorization(OperatorPolicy);
        g.MapPost("/{id:guid}/resolve", Resolve).RequireAuthorization(OperatorPolicy);
        g.MapPost("/{id:guid}/comment", Comment).RequireAuthorization(OperatorPolicy);
        return app;
    }

    private static IQueryable<IncidentDto> Query(SentinelDbContext db, Guid? scope, Guid? id = null) =>
        db.Incidents.AsNoTracking().Where(i => scope == null || i.TenantId == scope).Where(i => id == null || i.Id == id)
            .Select(i => new IncidentDto(i.Id, i.TenantId, i.Host!.Tenant!.Name, i.HostId, i.Host!.Name, i.CheckId, i.Key, i.Kind, i.Severity, i.Status,
                i.Title, i.Summary, i.OpenedAt, i.AcknowledgedAt, i.ResolvedAt, i.UpdatedAt, i.SnoozedUntil));

    private static async Task<IResult> List(HttpContext http, SentinelDbContext db, string? status, Guid? hostId, Guid? tenantId, int? take)
    {
        var scope = http.User.GetTenantScope() ?? tenantId;
        var q = db.Incidents.AsNoTracking().Where(i => scope == null || i.TenantId == scope);
        if (hostId is not null) q = q.Where(i => i.HostId == hostId);
        q = status switch
        {
            "active" or null => q.Where(i => i.Status != IncidentStatus.Resolved),
            "resolved" => q.Where(i => i.Status == IncidentStatus.Resolved),
            _ => q,
        };
        var list = await q.OrderByDescending(i => i.Status != IncidentStatus.Resolved).ThenByDescending(i => i.Severity).ThenByDescending(i => i.OpenedAt)
            .Take(Math.Clamp(take ?? 200, 1, 1000))
            .Select(i => new IncidentDto(i.Id, i.TenantId, i.Host!.Tenant!.Name, i.HostId, i.Host!.Name, i.CheckId, i.Key, i.Kind, i.Severity, i.Status,
                i.Title, i.Summary, i.OpenedAt, i.AcknowledgedAt, i.ResolvedAt, i.UpdatedAt, i.SnoozedUntil))
            .ToListAsync();
        return Results.Ok(list);
    }

    private static async Task<IResult> Summary(HttpContext http, SentinelDbContext db)
    {
        var scope = http.User.GetTenantScope();
        var q = db.Incidents.AsNoTracking().Where(i => scope == null || i.TenantId == scope);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        return Results.Ok(new IncidentSummaryDto(
            await q.CountAsync(i => i.Status == IncidentStatus.Open),
            await q.CountAsync(i => i.Status == IncidentStatus.Acknowledged || i.Status == IncidentStatus.InProgress),
            await q.CountAsync(i => i.Status != IncidentStatus.Resolved && i.Severity == CheckStatus.Critical),
            await q.CountAsync(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt >= today)));
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, SentinelDbContext db)
    {
        var dto = await Query(db, http.User.GetTenantScope(), id).FirstOrDefaultAsync();
        if (dto is null) return Results.NotFound();
        var events = await db.IncidentEvents.AsNoTracking().Where(e => e.IncidentId == id).OrderBy(e => e.At)
            .Select(e => new IncidentEventDto(e.At, e.Type, e.Message, e.UserId == null ? null : db.Users.Where(u => u.Id == e.UserId).Select(u => u.DisplayName).FirstOrDefault()))
            .ToListAsync();
        return Results.Ok(new IncidentDetailDto(dto, events));
    }

    private static async Task<IResult> Ack(Guid id, IncidentAction req, HttpContext http, SentinelDbContext db, IncidentService svc)
    {
        var i = await db.Incidents.FindAsync(id);
        if (i is null) return Results.NotFound();
        svc.Acknowledge(i, http.User.GetUserId(), req.Comment);
        Audit(db, http, "incident.ack", i);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> TakeInProgress(Guid id, TakeInProgressRequest req, HttpContext http, SentinelDbContext db, IncidentService svc)
    {
        var i = await db.Incidents.FindAsync(id);
        if (i is null) return Results.NotFound();
        if (i.Status == IncidentStatus.Resolved) return Results.BadRequest(new { error = "Инцидент уже закрыт." });

        var until = req.Until ?? DateTimeOffset.UtcNow.AddHours(Math.Clamp(req.Hours ?? 24, 1, 24 * 30));
        if (until <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "Срок должен быть в будущем." });

        svc.TakeInProgress(i, http.User.GetUserId(), until, req.Comment);
        Audit(db, http, "incident.in_progress", i);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Resolve(Guid id, IncidentAction req, HttpContext http, SentinelDbContext db, IncidentService svc)
    {
        var i = await db.Incidents.FindAsync(id);
        if (i is null) return Results.NotFound();
        svc.ResolveManually(i, http.User.GetUserId(), req.Comment);
        Audit(db, http, "incident.resolve", i);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Comment(Guid id, IncidentAction req, HttpContext http, SentinelDbContext db)
    {
        if (string.IsNullOrWhiteSpace(req.Comment)) return Results.BadRequest(new { error = "Пустой комментарий." });
        if (!await db.Incidents.AnyAsync(i => i.Id == id)) return Results.NotFound();
        db.IncidentEvents.Add(new IncidentEvent { IncidentId = id, At = DateTimeOffset.UtcNow, Type = "comment", Message = req.Comment.Trim(), UserId = http.User.GetUserId() });
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static void Audit(SentinelDbContext db, HttpContext http, string action, Incident i)
        => db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = i.TenantId, Action = action,
            TargetType = nameof(Incident), TargetId = i.Id, RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
}
