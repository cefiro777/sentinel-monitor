using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using static Sentinel.Server.Endpoints.AuthEndpoints;

namespace Sentinel.Server.Endpoints;

/// <summary>Сводка по видеонаблюдению: все устройства из проверок cctv.* с их состоянием и деталями.</summary>
public static class CctvEndpoints
{
    public sealed record DeviceDto(Guid CheckId, string ModuleId, string CheckName, Guid HostId, string HostName, Guid TenantId, string TenantName, bool AgentOnline,
        string Key, string Name, CheckStatus Status, string Summary, JsonElement? Details, DateTimeOffset? LastResultAt, DateTimeOffset? LastChangeAt);

    public sealed record ChannelRules(List<int> IgnoreChannels, List<int> MotionChannels);

    public static IEndpointRouteBuilder MapCctvEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/cctv", List).WithTags("Cctv").RequireAuthorization(UserPolicy);
        app.MapPut("/api/cctv/{checkId:guid}/{key}/channels", SetChannelRules).WithTags("Cctv").RequireAuthorization(OperatorPolicy);
        return app;
    }

    /// <summary>
    /// Режимы каналов устройства со страницы «Видеонаблюдение». Правим настройки проверки прямо в хранимом JSON:
    /// пароли там зашифрованы, и трогать их не нужно. Список «проверять только эти» (channels) при этом очищается — его заменяют правила.
    /// </summary>
    private static async Task<IResult> SetChannelRules(Guid checkId, string key, ChannelRules req, HttpContext http, SentinelDbContext db, IAgentMessenger messenger)
    {
        var scope = http.User.GetTenantScope();
        var check = await db.Checks.Include(c => c.Host!).ThenInclude(h => h.Agent).FirstOrDefaultAsync(c => c.Id == checkId && (scope == null || c.Host!.TenantId == scope));
        if (check is null || !check.ModuleId.StartsWith("cctv.")) return Results.NotFound();

        var root = JsonNode.Parse(check.SettingsJson) as JsonObject;
        var devices = root?["devices"] as JsonArray;
        var device = devices?.OfType<JsonObject>().FirstOrDefault(d => DeviceKey(d) == key);
        if (device is null) return Results.NotFound(new { error = $"Устройство {key} не найдено в настройках проверки." });

        device["ignoreChannels"] = new JsonArray(req.IgnoreChannels.Distinct().OrderBy(x => x).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        device["motionChannels"] = new JsonArray(req.MotionChannels.Where(x => !req.IgnoreChannels.Contains(x)).Distinct().OrderBy(x => x).Select(x => JsonValue.Create(x)).ToArray<JsonNode?>());
        device["channels"] = new JsonArray();
        check.SettingsJson = root!.ToJsonString();
        check.UpdatedAt = DateTimeOffset.UtcNow;

        var host = check.Host!;
        host.ConfigVersion++;
        db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow, UserId = http.User.GetUserId(), TenantId = host.TenantId, Action = "cctv.channels", TargetType = nameof(Check), TargetId = check.Id,
            DetailsJson = JsonSerializer.Serialize(new { device = key, ignore = req.IgnoreChannels, motion = req.MotionChannels }), RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync();
        if (host.Agent is not null) await messenger.NotifyConfigChangedAsync(host.Agent.Id, host.ConfigVersion);
        return Results.Ok(new { applied = host.Agent?.IsOnline == true });
    }

    /// <summary>Человекочитаемое имя устройства из настроек проверки («Склад», «Проходная»), если задано.</summary>
    private static string? DeviceName(string settingsJson, string key)
    {
        try
        {
            var devices = JsonNode.Parse(settingsJson)?["devices"] as JsonArray;
            var device = devices?.OfType<JsonObject>().FirstOrDefault(d => DeviceKey(d) == key);
            var name = device?["name"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
        }
        catch { return null; }
    }

    /// <summary>Тот же ключ, что CctvDevice.Key у агента: host, либо host:port, если порт не 80.</summary>
    private static string DeviceKey(JsonObject d)
    {
        var host = (d["host"]?.GetValue<string>() ?? "").Trim();
        var port = d["port"] is JsonValue v && v.TryGetValue<int>(out var p) ? p : 80;
        return port == 80 || port == 0 ? host : $"{host}:{port}";
    }

    private static async Task<IResult> List(HttpContext http, SentinelDbContext db)
    {
        var scope = http.User.GetTenantScope();
        var states = await db.CheckStates.AsNoTracking()
            .Where(s => s.Check!.ModuleId.StartsWith("cctv.") && (scope == null || s.Check.Host!.TenantId == scope))
            .OrderBy(s => s.Check!.Host!.Tenant!.Name).ThenBy(s => s.Check!.Host!.Name).ThenBy(s => s.Check!.Name).ThenBy(s => s.Key)
            .Select(s => new
            {
                s.CheckId, s.Check!.ModuleId, CheckName = s.Check.Name, s.Check.HostId, HostName = s.Check.Host!.Name, s.Check.Host.TenantId, TenantName = s.Check.Host.Tenant!.Name,
                AgentOnline = s.Check.Host.Agent != null && s.Check.Host.Agent.IsOnline, s.Key, s.Status, s.Summary, s.DetailsJson, s.LastResultAt, s.LastChangeAt,
                s.Check.SettingsJson,
            })
            .ToListAsync();
        return Results.Ok(states.Select(s => new DeviceDto(s.CheckId, s.ModuleId, s.CheckName, s.HostId, s.HostName, s.TenantId, s.TenantName, s.AgentOnline,
            s.Key, DeviceName(s.SettingsJson, s.Key) ?? s.Key, s.Status, s.Summary,
            s.DetailsJson is null ? null : JsonDocument.Parse(s.DetailsJson).RootElement.Clone(), s.LastResultAt, s.LastChangeAt)).ToList());
    }
}
