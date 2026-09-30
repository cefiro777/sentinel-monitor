using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Contracts.Protocol;
using Sentinel.Notifications;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Endpoints;

/// <summary>Каналы и маршруты оповещений. Секреты каналов наружу не отдаются — только маскированные настройки.</summary>
public static class NotificationEndpoints
{
    public sealed record ChannelDto(Guid Id, string Name, ChannelType Type, JsonElement Settings, bool IsEnabled, DateTimeOffset? LastUsedAt, string? LastError);
    public sealed record ChannelUpsert(string Name, ChannelType Type, JsonElement Settings, bool IsEnabled);
    public sealed record RouteDto(Guid Id, Guid ChannelId, string ChannelName, Guid? TenantId, string? TenantName, CheckStatus MinSeverity, bool NotifyOnResolve, int? QuietFromHour, int? QuietToHour, int RemindMinutes, bool IsEnabled);
    public sealed record RouteUpsert(Guid ChannelId, Guid? TenantId, CheckStatus MinSeverity, bool NotifyOnResolve, int? QuietFromHour, int? QuietToHour, int? RemindMinutes, bool IsEnabled);
    public sealed record LogDto(long Id, Guid IncidentId, string IncidentTitle, Guid ChannelId, string ChannelName, NotificationKind Kind, DateTimeOffset At, bool Success, string? Error);

    private static readonly string[] SecretFields = ["password", "botToken", "token", "apiKey"];

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var ch = app.MapGroup("/api/notifications/channels").WithTags("Notifications").RequireAuthorization(AdminPolicy);
        ch.MapGet("/", ListChannels);
        ch.MapPost("/", CreateChannel);
        ch.MapPut("/{id:guid}", UpdateChannel);
        ch.MapDelete("/{id:guid}", DeleteChannel);
        ch.MapPost("/{id:guid}/test", TestChannel);

        var rt = app.MapGroup("/api/notifications/routes").WithTags("Notifications").RequireAuthorization(AdminPolicy);
        rt.MapGet("/", ListRoutes);
        rt.MapPost("/", CreateRoute);
        rt.MapPut("/{id:guid}", UpdateRoute);
        rt.MapDelete("/{id:guid}", DeleteRoute);

        app.MapGet("/api/notifications/log", ListLog).WithTags("Notifications").RequireAuthorization(AdminPolicy);
        return app;
    }

    // ---- каналы ----

    private static async Task<IResult> ListChannels(SentinelDbContext db, ISecretProtector protector)
    {
        var list = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        return Results.Ok(list.Select(c => ToDto(c, protector)).ToList());
    }

    private static async Task<IResult> CreateChannel(ChannelUpsert req, SentinelDbContext db, ISecretProtector protector, HttpContext http)
    {
        var c = new NotificationChannel { Id = Guid.NewGuid(), Name = req.Name.Trim(), Type = req.Type, IsEnabled = req.IsEnabled, CreatedAt = DateTimeOffset.UtcNow };
        c.SettingsEncrypted = protector.Protect(Encoding.UTF8.GetBytes(req.Settings.GetRawText()));
        db.NotificationChannels.Add(c);
        Audit(db, http, "channel.create", c.Id);
        await db.SaveChangesAsync();
        return Results.Created($"/api/notifications/channels/{c.Id}", ToDto(c, protector));
    }

    private static async Task<IResult> UpdateChannel(Guid id, ChannelUpsert req, SentinelDbContext db, ISecretProtector protector, HttpContext http)
    {
        var c = await db.NotificationChannels.FindAsync(id);
        if (c is null) return Results.NotFound();
        c.Name = req.Name.Trim();
        c.Type = req.Type;
        c.IsEnabled = req.IsEnabled;
        // Маскированные секреты («••••••») из формы не перетирают сохранённые значения.
        var merged = MergeSecrets(req.Settings, JsonDocument.Parse(Encoding.UTF8.GetString(protector.Unprotect(c.SettingsEncrypted))).RootElement);
        c.SettingsEncrypted = protector.Protect(Encoding.UTF8.GetBytes(merged));
        Audit(db, http, "channel.update", c.Id);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(c, protector));
    }

    private static async Task<IResult> DeleteChannel(Guid id, SentinelDbContext db, HttpContext http)
    {
        var c = await db.NotificationChannels.FindAsync(id);
        if (c is null) return Results.NotFound();
        db.NotificationChannels.Remove(c);
        Audit(db, http, "channel.delete", c.Id);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> TestChannel(Guid id, SentinelDbContext db, ISecretProtector protector, IEnumerable<INotificationSender> senders,
        IOptions<SentinelOptions> options, CancellationToken ct)
    {
        var c = await db.NotificationChannels.FindAsync([id], ct);
        if (c is null) return Results.NotFound();
        var sender = senders.FirstOrDefault(s => s.Type == c.Type);
        if (sender is null) return Results.BadRequest(new { error = $"Тип {c.Type} не поддерживается." });

        var fake = new Incident
        {
            Id = Guid.Empty, Title = "Тестовое уведомление", Summary = $"Канал «{c.Name}» настроен верно.", Severity = CheckStatus.Warning,
            OpenedAt = DateTimeOffset.UtcNow, Host = new Host { Name = "-", Tenant = new Tenant { Name = "Sentinel" } },
        };
        try
        {
            await sender.SendAsync(Encoding.UTF8.GetString(protector.Unprotect(c.SettingsEncrypted)), NotificationDispatcher.Format(fake, NotificationKind.Test, options.Value.PublicUrl), ct);
            c.LastUsedAt = DateTimeOffset.UtcNow;
            c.LastError = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            c.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // ---- маршруты ----

    private static IQueryable<RouteDto> RouteQuery(SentinelDbContext db, Guid? id = null) =>
        db.NotificationRoutes.AsNoTracking().Where(r => id == null || r.Id == id).Select(r => new RouteDto(r.Id, r.ChannelId, r.Channel!.Name, r.TenantId,
            r.TenantId == null ? null : db.Tenants.Where(t => t.Id == r.TenantId).Select(t => t.Name).FirstOrDefault(),
            r.MinSeverity, r.NotifyOnResolve, r.QuietFromHour, r.QuietToHour, r.RemindMinutes, r.IsEnabled));

    private static async Task<IResult> ListRoutes(SentinelDbContext db) => Results.Ok(await RouteQuery(db).ToListAsync());

    private static async Task<IResult> CreateRoute(RouteUpsert req, SentinelDbContext db, HttpContext http)
    {
        if (!await db.NotificationChannels.AnyAsync(c => c.Id == req.ChannelId)) return Results.BadRequest(new { error = "Канал не найден." });
        var r = new NotificationRoute { Id = Guid.NewGuid() };
        Apply(r, req);
        db.NotificationRoutes.Add(r);
        Audit(db, http, "route.create", r.Id);
        await db.SaveChangesAsync();
        return Results.Created($"/api/notifications/routes/{r.Id}", await RouteQuery(db, r.Id).FirstAsync());
    }

    private static async Task<IResult> UpdateRoute(Guid id, RouteUpsert req, SentinelDbContext db, HttpContext http)
    {
        var r = await db.NotificationRoutes.FindAsync(id);
        if (r is null) return Results.NotFound();
        Apply(r, req);
        Audit(db, http, "route.update", r.Id);
        await db.SaveChangesAsync();
        return Results.Ok(await RouteQuery(db, r.Id).FirstAsync());
    }

    private static async Task<IResult> DeleteRoute(Guid id, SentinelDbContext db, HttpContext http)
    {
        var r = await db.NotificationRoutes.FindAsync(id);
        if (r is null) return Results.NotFound();
        db.NotificationRoutes.Remove(r);
        Audit(db, http, "route.delete", r.Id);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> ListLog(SentinelDbContext db, int? take)
    {
        var list = await db.NotificationLog.AsNoTracking().OrderByDescending(l => l.At).Take(Math.Clamp(take ?? 100, 1, 500))
            .Select(l => new LogDto(l.Id, l.IncidentId, db.Incidents.Where(i => i.Id == l.IncidentId).Select(i => i.Title).FirstOrDefault() ?? "",
                l.ChannelId, db.NotificationChannels.Where(c => c.Id == l.ChannelId).Select(c => c.Name).FirstOrDefault() ?? "", l.Kind, l.At, l.Success, l.Error))
            .ToListAsync();
        return Results.Ok(list);
    }

    // ---- helpers ----

    private static void Apply(NotificationRoute r, RouteUpsert req)
    {
        r.ChannelId = req.ChannelId;
        r.TenantId = req.TenantId;
        r.MinSeverity = req.MinSeverity;
        r.NotifyOnResolve = req.NotifyOnResolve;
        r.QuietFromHour = req.QuietFromHour is >= 0 and <= 23 ? req.QuietFromHour : null;
        r.QuietToHour = req.QuietToHour is >= 0 and <= 23 ? req.QuietToHour : null;
        r.RemindMinutes = Math.Clamp(req.RemindMinutes ?? 0, 0, 24 * 60);
        r.IsEnabled = req.IsEnabled;
    }

    private static ChannelDto ToDto(NotificationChannel c, ISecretProtector protector)
    {
        var json = Encoding.UTF8.GetString(protector.Unprotect(c.SettingsEncrypted));
        var masked = new Dictionary<string, object?>();
        foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
            masked[p.Name] = SecretFields.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length > 0
                ? "••••••" : p.Value.Clone();
        return new ChannelDto(c.Id, c.Name, c.Type, JsonSerializer.SerializeToElement(masked), c.IsEnabled, c.LastUsedAt, c.LastError);
    }

    private static string MergeSecrets(JsonElement incoming, JsonElement stored)
    {
        var result = new Dictionary<string, object?>();
        foreach (var p in incoming.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "••••••" && stored.TryGetProperty(p.Name, out var old))
                result[p.Name] = old.Clone();
            else
                result[p.Name] = p.Value.Clone();
        }
        return JsonSerializer.Serialize(result);
    }

    private static void Audit(SentinelDbContext db, HttpContext http, string action, Guid targetId)
        => db.AuditLog.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), Action = action, TargetType = "Notification", TargetId = targetId });
}
