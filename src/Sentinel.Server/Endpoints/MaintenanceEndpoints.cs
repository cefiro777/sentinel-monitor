using Microsoft.EntityFrameworkCore;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using static Sentinel.Server.Endpoints.AuthEndpoints;

namespace Sentinel.Server.Endpoints;

/// <summary>Окна обслуживания: на время плановых работ уведомления по хосту/клиенту не шлются.</summary>
public static class MaintenanceEndpoints
{
    public sealed record WindowDto(Guid Id, Guid? TenantId, string? TenantName, Guid? HostId, string? HostName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Comment, bool Active);
    public sealed record WindowUpsert(Guid? TenantId, Guid? HostId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Comment);

    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance").WithTags("Maintenance").RequireAuthorization(UserPolicy);
        g.MapGet("/", List);
        g.MapPost("/", Create).RequireAuthorization(OperatorPolicy);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(OperatorPolicy);
        return app;
    }

    private static async Task<IResult> List(HttpContext http, SentinelDbContext db, Guid? hostId, bool? activeOnly)
    {
        var scope = http.User.GetTenantScope();
        var now = DateTimeOffset.UtcNow;
        var q = db.MaintenanceWindows.AsNoTracking().Where(m => m.EndsAt > now.AddDays(-7));
        if (activeOnly == true) q = q.Where(m => m.StartsAt <= now && m.EndsAt >= now);
        if (hostId is not null) q = q.Where(m => m.HostId == hostId || (m.HostId == null && m.TenantId == db.Hosts.Where(h => h.Id == hostId).Select(h => h.TenantId).FirstOrDefault()));
        var list = await q.OrderByDescending(m => m.StartsAt)
            .Select(m => new WindowDto(m.Id, m.TenantId, m.TenantId == null ? null : db.Tenants.Where(t => t.Id == m.TenantId).Select(t => t.Name).FirstOrDefault(),
                m.HostId, m.HostId == null ? null : db.Hosts.Where(h => h.Id == m.HostId).Select(h => h.Name).FirstOrDefault(),
                m.StartsAt, m.EndsAt, m.Comment, m.StartsAt <= now && m.EndsAt >= now))
            .ToListAsync();
        return Results.Ok(scope is null ? list : list.Where(w => w.TenantId == scope || (w.HostId != null && db.Hosts.Any(h => h.Id == w.HostId && h.TenantId == scope))).ToList());
    }

    private static async Task<IResult> Create(WindowUpsert req, HttpContext http, SentinelDbContext db)
    {
        if (req.EndsAt <= req.StartsAt) return Results.BadRequest(new { error = "Окончание должно быть позже начала." });
        Guid? tenantId = req.TenantId;
        if (req.HostId is not null)
        {
            tenantId = await db.Hosts.Where(h => h.Id == req.HostId).Select(h => (Guid?)h.TenantId).FirstOrDefaultAsync();
            if (tenantId is null) return Results.BadRequest(new { error = "Хост не найден." });
        }
        else if (tenantId is null || !await db.Tenants.AnyAsync(t => t.Id == tenantId)) return Results.BadRequest(new { error = "Укажите хост или клиента." });

        var m = new MaintenanceWindow
        {
            Id = Guid.NewGuid(), TenantId = tenantId, HostId = req.HostId, StartsAt = req.StartsAt, EndsAt = req.EndsAt,
            Comment = (req.Comment ?? "").Trim(), CreatedByUserId = http.User.GetUserId(), CreatedAt = DateTimeOffset.UtcNow,
        };
        db.MaintenanceWindows.Add(m);
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = tenantId, Action = "maintenance.create", TargetType = nameof(MaintenanceWindow), TargetId = m.Id,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { req.HostId, req.StartsAt, req.EndsAt, req.Comment }) });
        await db.SaveChangesAsync();
        return Results.Created($"/api/maintenance/{m.Id}", new { m.Id });
    }

    private static async Task<IResult> Delete(Guid id, HttpContext http, SentinelDbContext db)
    {
        var m = await db.MaintenanceWindows.FindAsync(id);
        if (m is null) return Results.NotFound();
        db.MaintenanceWindows.Remove(m);
        db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = m.TenantId, Action = "maintenance.delete", TargetType = nameof(MaintenanceWindow), TargetId = m.Id });
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
}
