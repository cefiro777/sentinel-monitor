using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Endpoints;

/// <summary>Шаблоны наборов проверок: создать из хоста, отредактировать, применить к нескольким хостам разом.</summary>
public static class TemplateEndpoints
{
    public sealed record TemplateItem(string ModuleId, string Name, bool Enabled, int IntervalSeconds, int? ConfirmCount, JsonElement Settings, List<string>? IgnoredKeys = null, int? AlertDelayMinutes = null);
    public sealed record TemplateDto(Guid Id, Guid? TenantId, string? TenantName, string Name, string Description, List<TemplateItem> Items, DateTimeOffset UpdatedAt);
    public sealed record TemplateUpsert(Guid? TenantId, string Name, string? Description, List<TemplateItem> Items);
    public sealed record FromHostRequest(Guid HostId, Guid? TenantId, string Name, string? Description);
    public sealed record ApplyRequest(List<Guid> HostIds, bool Overwrite);
    public sealed record ApplyHostResult(Guid HostId, string HostName, int Created, int Updated, int Skipped);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/check-templates").WithTags("Templates").RequireAuthorization(UserPolicy);
        g.MapGet("/", List);
        g.MapPost("/", Create).RequireAuthorization(OperatorPolicy);
        g.MapPost("/from-host", CreateFromHost).RequireAuthorization(OperatorPolicy);
        g.MapPut("/{id:guid}", Update).RequireAuthorization(OperatorPolicy);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(OperatorPolicy);
        g.MapPost("/{id:guid}/apply", Apply).RequireAuthorization(OperatorPolicy);
        // Наборы служб по ролям — для кнопки «Добавить набор» в форме «Службы Windows».
        app.MapGet("/api/service-presets", (ServicePresets presets) => Results.Ok(presets.All)).WithTags("Templates").RequireAuthorization(UserPolicy);
        // База подсказок по событиям журналов — для расшифровки ошибок в карточке проверки «Журнал событий».
        app.MapGet("/api/event-hints", (EventHints hints) => Results.Ok(hints.All)).WithTags("Templates").RequireAuthorization(UserPolicy);
        return app;
    }

    private static async Task<IResult> List(HttpContext http, SentinelDbContext db, SettingsProtector sp)
    {
        var scope = http.User.GetTenantScope();
        var list = await db.CheckTemplates.AsNoTracking().Include(t => t.Tenant)
            .Where(t => scope == null || t.TenantId == null || t.TenantId == scope)
            .OrderBy(t => t.TenantId == null ? 0 : 1).ThenBy(t => t.Name).ToListAsync();
        return Results.Ok(list.Select(t => ToDto(t, sp)).ToList());
    }

    private static async Task<IResult> Create(TemplateUpsert req, HttpContext http, SentinelDbContext db, SettingsProtector sp)
    {
        if (Validate(req) is { } err) return Results.BadRequest(new { error = err });
        var now = DateTimeOffset.UtcNow;
        var t = new CheckTemplate
        {
            Id = Guid.NewGuid(), TenantId = req.TenantId, Name = req.Name.Trim(), Description = req.Description?.Trim() ?? "",
            ItemsJson = ProtectItems(req.Items, null, sp), CreatedAt = now, UpdatedAt = now,
        };
        db.CheckTemplates.Add(t);
        Audit(db, http, "template.create", t);
        await db.SaveChangesAsync();
        return Results.Created($"/api/check-templates/{t.Id}", ToDto(t, sp));
    }

    /// <summary>Снимок проверок хоста → шаблон. Зашифрованные настройки копируются как есть (тот же ключ), секреты не раскрываются.</summary>
    private static async Task<IResult> CreateFromHost(FromHostRequest req, HttpContext http, SentinelDbContext db, SettingsProtector sp)
    {
        var host = await db.Hosts.AsNoTracking().Include(h => h.Checks).FirstOrDefaultAsync(h => h.Id == req.HostId);
        if (host is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Укажите название шаблона." });
        if (host.Checks.Count == 0) return Results.BadRequest(new { error = "На хосте нет проверок." });

        var items = host.Checks.OrderBy(c => c.Name).Select(c => new StoredItem(c.ModuleId, c.Name, c.Enabled, c.IntervalSeconds, c.ConfirmCount, JsonDocument.Parse(c.SettingsJson).RootElement.Clone(), c.IgnoredKeys.ToList(), c.AlertDelayMinutes)).ToList();
        var now = DateTimeOffset.UtcNow;
        var t = new CheckTemplate
        {
            Id = Guid.NewGuid(), TenantId = req.TenantId, Name = req.Name.Trim(), Description = req.Description?.Trim() ?? $"Снято с хоста {host.Name}",
            ItemsJson = JsonSerializer.Serialize(items, Json), CreatedAt = now, UpdatedAt = now,
        };
        db.CheckTemplates.Add(t);
        Audit(db, http, "template.create", t);
        await db.SaveChangesAsync();
        return Results.Created($"/api/check-templates/{t.Id}", ToDto(t, sp));
    }

    private static async Task<IResult> Update(Guid id, TemplateUpsert req, HttpContext http, SentinelDbContext db, SettingsProtector sp)
    {
        var t = await db.CheckTemplates.Include(x => x.Tenant).FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Results.NotFound();
        if (Validate(req) is { } err) return Results.BadRequest(new { error = err });
        t.TenantId = req.TenantId;
        t.Name = req.Name.Trim();
        t.Description = req.Description?.Trim() ?? "";
        t.ItemsJson = ProtectItems(req.Items, t.ItemsJson, sp);
        t.UpdatedAt = DateTimeOffset.UtcNow;
        Audit(db, http, "template.update", t);
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(t, sp));
    }

    private static async Task<IResult> Delete(Guid id, HttpContext http, SentinelDbContext db)
    {
        var t = await db.CheckTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Results.NotFound();
        db.CheckTemplates.Remove(t);
        Audit(db, http, "template.delete", t);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    /// <summary>
    /// Применить к хостам. Проверка хоста считается «той же», если совпали модуль и название:
    /// её либо пропускаем, либо (Overwrite) перезаписываем настройками шаблона. Остальные проверки хоста не трогаем.
    /// </summary>
    private static async Task<IResult> Apply(Guid id, ApplyRequest req, HttpContext http, SentinelDbContext db, IAgentMessenger messenger)
    {
        var t = await db.CheckTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Results.NotFound();
        var items = JsonSerializer.Deserialize<List<StoredItem>>(t.ItemsJson, Json) ?? new();
        if (items.Count == 0) return Results.BadRequest(new { error = "В шаблоне нет проверок." });
        if (req.HostIds.Count == 0) return Results.BadRequest(new { error = "Не выбраны хосты." });

        var hosts = await db.Hosts.Include(h => h.Agent).Include(h => h.Checks)
            .Where(h => req.HostIds.Contains(h.Id) && (t.TenantId == null || h.TenantId == t.TenantId)).ToListAsync();
        var results = new List<ApplyHostResult>();
        var now = DateTimeOffset.UtcNow;
        var notify = new List<(Guid agentId, int version)>();

        foreach (var host in hosts)
        {
            int created = 0, updated = 0, skipped = 0;
            foreach (var item in items)
            {
                var existing = host.Checks.FirstOrDefault(c => c.ModuleId == item.ModuleId && string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                var settingsJson = item.Settings.GetRawText();
                if (existing is null)
                {
                    // Через DbSet, а не host.Checks: сущность с заданным Id, найденная через навигацию, EF считает изменённой, а не новой.
                    db.Checks.Add(new Check
                    {
                        Id = Guid.NewGuid(), HostId = host.Id, ModuleId = item.ModuleId, Name = item.Name, Enabled = item.Enabled,
                        IntervalSeconds = item.IntervalSeconds, ConfirmCount = item.ConfirmCount, SettingsJson = settingsJson, CreatedAt = now, UpdatedAt = now,
                        IgnoredKeys = item.IgnoredKeys?.ToList() ?? new(),
                        AlertDelayMinutes = InventoryEndpoints.ClampDelay(item.AlertDelayMinutes ?? InventoryEndpoints.DefaultAlertDelay(item.ModuleId)),
                    });
                    created++;
                }
                else if (!req.Overwrite) skipped++;
                else
                {
                    existing.Enabled = item.Enabled;
                    existing.IntervalSeconds = item.IntervalSeconds;
                    existing.ConfirmCount = item.ConfirmCount;
                    if (item.AlertDelayMinutes is int delay) existing.AlertDelayMinutes = InventoryEndpoints.ClampDelay(delay);
                    existing.SettingsJson = settingsJson;   // состояния не сбрасываем — устаревшие ключи уберёт ResultIngestService
                    if (item.IgnoredKeys is { Count: > 0 }) existing.IgnoredKeys = existing.IgnoredKeys.Union(item.IgnoredKeys).ToList();
                    existing.UpdatedAt = now;
                    updated++;
                }
            }
            if (created + updated > 0)
            {
                host.ConfigVersion++;
                if (host.Agent is not null) notify.Add((host.Agent.Id, host.ConfigVersion));
                db.AuditLog.Add(new AuditEntry
                {
                    At = now, UserId = http.User.GetUserId(), TenantId = host.TenantId, Action = "template.apply", TargetType = nameof(Host), TargetId = host.Id,
                    DetailsJson = JsonSerializer.Serialize(new { template = t.Name, created, updated, skipped }), RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
                });
            }
            results.Add(new ApplyHostResult(host.Id, host.Name, created, updated, skipped));
        }
        await db.SaveChangesAsync();
        foreach (var (agentId, version) in notify) await messenger.NotifyConfigChangedAsync(agentId, version);
        return Results.Ok(results);
    }

    // ---- helpers ----

    /// <summary>Элемент в jsonb: как TemplateItem, но ConfirmCount уже нормализован.</summary>
    private sealed record StoredItem(string ModuleId, string Name, bool Enabled, int IntervalSeconds, int ConfirmCount, JsonElement Settings, List<string>? IgnoredKeys = null, int? AlertDelayMinutes = null);

    private static string? Validate(TemplateUpsert req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Укажите название шаблона.";
        if (req.Items.Count == 0) return "Добавьте хотя бы одну проверку.";
        foreach (var i in req.Items)
        {
            if (string.IsNullOrWhiteSpace(i.ModuleId) || string.IsNullOrWhiteSpace(i.Name)) return "У каждой проверки должны быть модуль и название.";
            if (i.IntervalSeconds < 10) return $"«{i.Name}»: интервал не меньше 10 секунд.";
        }
        var dup = req.Items.GroupBy(i => (i.ModuleId.Trim(), i.Name.Trim().ToLowerInvariant())).FirstOrDefault(g => g.Count() > 1);
        return dup is null ? null : $"Проверка «{dup.First().Name}» ({dup.Key.Item1}) встречается дважды — названия внутри одного модуля должны различаться.";
    }

    /// <summary>Шифрует секреты в каждом элементе; маска «••••••» заменяется прежним значением того же элемента (по модулю + названию).</summary>
    private static string ProtectItems(List<TemplateItem> items, string? storedJson, SettingsProtector sp)
    {
        var stored = storedJson is null ? new() : JsonSerializer.Deserialize<List<StoredItem>>(storedJson, Json) ?? new();
        var result = items.Select(i =>
        {
            var old = stored.FirstOrDefault(s => s.ModuleId == i.ModuleId.Trim() && string.Equals(s.Name, i.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            var protectedJson = sp.ProtectForStorage(i.Settings, old?.Settings.GetRawText());
            return new StoredItem(i.ModuleId.Trim(), i.Name.Trim(), i.Enabled, i.IntervalSeconds, Math.Clamp(i.ConfirmCount ?? 2, 1, 10), JsonDocument.Parse(protectedJson).RootElement.Clone(),
                i.IgnoredKeys?.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList(),
                i.AlertDelayMinutes is int d ? InventoryEndpoints.ClampDelay(d) : null);
        }).ToList();
        return JsonSerializer.Serialize(result, Json);
    }

    private static TemplateDto ToDto(CheckTemplate t, SettingsProtector sp)
    {
        var items = (JsonSerializer.Deserialize<List<StoredItem>>(t.ItemsJson, Json) ?? new())
            .Select(i => new TemplateItem(i.ModuleId, i.Name, i.Enabled, i.IntervalSeconds, i.ConfirmCount, sp.MaskSecrets(i.Settings.GetRawText()), i.IgnoredKeys ?? new(), i.AlertDelayMinutes ?? InventoryEndpoints.DefaultAlertDelay(i.ModuleId))).ToList();
        return new TemplateDto(t.Id, t.TenantId, t.Tenant?.Name, t.Name, t.Description, items, t.UpdatedAt);
    }

    private static void Audit(SentinelDbContext db, HttpContext http, string action, CheckTemplate t)
        => db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = t.TenantId, Action = action,
            TargetType = nameof(CheckTemplate), TargetId = t.Id, DetailsJson = JsonSerializer.Serialize(new { name = t.Name }), RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
}
