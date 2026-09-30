using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;

namespace Sentinel.Server.Endpoints;

/// <summary>Сводка по заданиям бэкапа и история запусков.</summary>
public static class BackupEndpoints
{
    public sealed record JobDto(Guid CheckId, Guid HostId, string HostName, Guid TenantId, string TenantName, string Name, string ModuleId, bool Enabled,
        string ScheduleText, string Target, string Destination, string? CloudFolder, bool CloudEnabled, bool AgentOnline,
        RunDto? LastRun, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastCloudAt, bool Overdue);
    public sealed record RunDto(long Id, string Kind, string Target, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Success, string? ArtifactPath,
        long SizeBytes, bool Verified, bool CloudEnabled, bool CloudUploaded, string? CloudPath, string? Error, string? Log);

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backups").WithTags("Backups").RequireAuthorization(UserPolicy);
        g.MapGet("/", ListJobs);
        g.MapGet("/{checkId:guid}/runs", ListRuns);
        g.MapPost("/{checkId:guid}/run", RunNow).RequireAuthorization(MfaOperatorPolicy);
        return app;
    }

    private static async Task<IResult> ListJobs(HttpContext http, SentinelDbContext db)
    {
        var scope = http.User.GetTenantScope();
        var checks = await db.Checks.AsNoTracking().Include(c => c.Host).ThenInclude(h => h!.Tenant).Include(c => c.Host).ThenInclude(h => h!.Agent)
            .Where(c => c.ModuleId.StartsWith("backup.") && (scope == null || c.Host!.TenantId == scope))
            .OrderBy(c => c.Host!.Tenant!.Name).ThenBy(c => c.Host!.Name).ThenBy(c => c.Name)
            .ToListAsync();
        var ids = checks.Select(c => c.Id).ToList();
        var overdue = await db.Incidents.Where(i => i.Kind == IncidentKind.BackupOverdue && i.Status != IncidentStatus.Resolved && ids.Contains(i.CheckId!.Value))
            .Select(i => new { i.CheckId, i.Key }).ToListAsync();
        var overdueSet = overdue.Select(o => (o.CheckId!.Value, o.Key)).ToHashSet();

        var jobs = new List<JobDto>();
        foreach (var c in checks)
        {
            var (scheduleText, targets, cloud, destination, cloudFolder) = Describe(c.SettingsJson);
            foreach (var target in targets)
            {
                var runs = await db.BackupRuns.AsNoTracking().Where(r => r.CheckId == c.Id && r.Target == target).OrderByDescending(r => r.FinishedAt).Take(30).ToListAsync();
                var last = runs.FirstOrDefault();
                jobs.Add(new JobDto(c.Id, c.HostId, c.Host!.Name, c.Host.TenantId, c.Host.Tenant!.Name, c.Name, c.ModuleId, c.Enabled, scheduleText, target, destination, cloudFolder, cloud,
                    c.Host.Agent?.IsOnline ?? false, last is null ? null : ToDto(last, includeLog: false),
                    runs.FirstOrDefault(r => r.Success)?.FinishedAt, runs.FirstOrDefault(r => r.CloudUploaded)?.FinishedAt, overdueSet.Contains((c.Id, target))));
            }
        }
        return Results.Ok(jobs);
    }

    private static async Task<IResult> ListRuns(Guid checkId, HttpContext http, SentinelDbContext db, int? take)
    {
        var scope = http.User.GetTenantScope();
        if (!await db.Checks.AnyAsync(c => c.Id == checkId && (scope == null || c.Host!.TenantId == scope))) return Results.NotFound();
        var runs = await db.BackupRuns.AsNoTracking().Where(r => r.CheckId == checkId).OrderByDescending(r => r.FinishedAt).Take(Math.Clamp(take ?? 50, 1, 500)).ToListAsync();
        return Results.Ok(runs.Select(r => ToDto(r, includeLog: true)).ToList());
    }

    private static async Task<IResult> RunNow(Guid checkId, HttpContext http, SentinelDbContext db, CommandService commands, CancellationToken ct)
    {
        var check = await db.Checks.Include(c => c.Host).ThenInclude(h => h!.Agent).FirstOrDefaultAsync(c => c.Id == checkId, ct);
        if (check is null) return Results.NotFound();
        if (check.Host?.Agent is null) return Results.BadRequest(new { error = "На хосте нет агента." });
        var payload = JsonSerializer.SerializeToElement(new { checkId });
        var cmd = await commands.IssueAsync(check.Host.Agent.Id, CommandTypes.BackupRun, payload, http.User.GetUserId(), ct);
        return Results.Accepted($"/api/hosts/{check.HostId}/commands", new { cmd.Id, cmd.Status });
    }

    private static (string schedule, List<string> targets, bool cloud, string destination, string? cloudFolder) Describe(string settingsJson)
    {
        try
        {
            var root = JsonDocument.Parse(settingsJson).RootElement;
            var sch = root.TryGetProperty("schedule", out var s) ? JsonSerializer.Deserialize<BackupSchedule>(s.GetRawText(), SentinelJson.Options) ?? new() : new();
            var text = sch.Type.ToLowerInvariant() switch
            {
                "hourly" => $"каждые {sch.EveryHours} ч",
                "weekly" => $"по дням {string.Join(",", sch.DaysOfWeek)} в {sch.Time}",
                _ => $"ежедневно в {sch.Time}",
            };
            var targets = root.TryGetProperty("databases", out var dbs) && dbs.ValueKind == JsonValueKind.Array
                ? dbs.EnumerateArray().Select(d => d.GetString() ?? "").Where(d => d.Length > 0).ToList() : new List<string> { "" };
            if (targets.Count == 0) targets.Add("");
            var cloud = root.TryGetProperty("cloud", out var c) && c.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            var destination = root.TryGetProperty("destination", out var d) && d.TryGetProperty("path", out var dp) ? dp.GetString() ?? "" : "";
            // В облаке к корню добавляются {клиент}/{хост}/{задание} — показываем корень, полный путь виден в истории запусков.
            var cloudFolder = cloud && c.TryGetProperty("rootFolder", out var rf) ? rf.GetString() : null;
            return (text, targets, cloud, destination, cloudFolder);
        }
        catch { return ("?", new List<string> { "" }, false, "", null); }
    }

    private static RunDto ToDto(BackupRun r, bool includeLog) => new(r.Id, r.Kind, r.Target, r.StartedAt, r.FinishedAt, r.Success, r.ArtifactPath, r.SizeBytes,
        r.Verified, r.CloudEnabled, r.CloudUploaded, r.CloudPath, r.Error, includeLog ? r.Log : null);
}
