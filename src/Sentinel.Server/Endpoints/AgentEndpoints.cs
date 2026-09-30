using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;
using Sentinel.Contracts.Security;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Options;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using Host = Sentinel.Server.Core.Entities.Host;

namespace Sentinel.Server.Endpoints;

/// <summary>API для агентов: /api/agent/*</summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/agent").WithTags("Agent");

        g.MapPost("/enroll", Enroll).AllowAnonymous();
        g.MapGet("/config", GetConfig).RequireAuthorization(AgentPolicy);
        g.MapPost("/results", PostResults).RequireAuthorization(AgentPolicy);
        g.MapGet("/time", () => Results.Ok(new { serverTime = DateTimeOffset.UtcNow })).AllowAnonymous();

        return app;
    }

    public const string AgentPolicy = "AgentOnly";

    private static async Task<IResult> Enroll(
        EnrollRequest req, SentinelDbContext db, ISecretProtector protector, ICommandSigner signer,
        IOptions<SentinelOptions> options, CertificatePinProvider pins, HttpContext http, ILogger<EnrollRequest> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Hostname))
            return Results.BadRequest(new { error = "Нужны token и hostname." });

        var hash = AgentAuth.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(req.Token.Trim()));
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (token is null || token.UsedAt is not null || token.ExpiresAt < now)
        {
            log.LogWarning("Неудачная попытка enrollment с {Ip}: {Host}", http.Connection.RemoteIpAddress, req.Hostname);
            return Results.Unauthorized();
        }

        Host? host = null;
        if (token.HostId is { } hostId)
            host = await db.Hosts.Include(h => h.Agent).FirstOrDefaultAsync(h => h.Id == hostId && h.TenantId == token.TenantId, ct);
        host ??= await db.Hosts.Include(h => h.Agent)
            .FirstOrDefaultAsync(h => h.TenantId == token.TenantId && h.Name == req.Hostname && h.Agent == null, ct);
        if (host is null)
        {
            host = new Host
            {
                Id = Guid.NewGuid(), TenantId = token.TenantId, SiteId = token.SiteId,
                Name = req.Hostname, Kind = HostKind.Server, CreatedAt = now,
            };
            db.Hosts.Add(host);
        }

        // Переустановка агента на том же хосте: старая запись заменяется, история проверок сохраняется.
        if (host.Agent is not null) db.Agents.Remove(host.Agent);

        var secret = RandomNumberGenerator.GetBytes(32);
        var agent = new Agent
        {
            Id = Guid.NewGuid(), HostId = host.Id, SecretEncrypted = protector.Protect(secret),
            AgentVersion = req.AgentVersion, OsVersion = req.OsVersion, MachineId = req.MachineId, Is64Bit = req.Is64Bit,
            EnrolledAt = now, LastSeenAt = now, IsOnline = false,
        };
        db.Agents.Add(agent);

        if (!await db.Checks.AnyAsync(c => c.HostId == host.Id, ct))
        {
            db.Checks.Add(new Check
            {
                Id = Guid.NewGuid(), HostId = host.Id, ModuleId = ModuleIds.System, Name = "Система",
                IntervalSeconds = 60, SettingsJson = JsonSerializer.Serialize(new SystemModuleSettings()),
                AlertDelayMinutes = InventoryEndpoints.DefaultAlertDelay(ModuleIds.System),
                CreatedAt = now, UpdatedAt = now,
            });
        }

        token.UsedAt = now;
        token.UsedByAgentId = agent.Id;
        db.AuditLog.Add(new AuditEntry
        {
            At = now, AgentId = agent.Id, TenantId = token.TenantId, Action = "agent.enroll",
            TargetType = nameof(Host), TargetId = host.Id, RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
            DetailsJson = JsonSerializer.Serialize(new { req.Hostname, req.OsVersion, req.AgentVersion }),
        });
        await db.SaveChangesAsync(ct);

        log.LogInformation("Агент {AgentId} зарегистрирован на хосте {Host} ({Tenant})", agent.Id, host.Name, token.TenantId);
        return Results.Ok(new EnrollResponse
        {
            AgentId = agent.Id, HostId = host.Id, Secret = Convert.ToBase64String(secret),
            ServerPublicKey = signer.PublicKeyBase64, CertificatePin = pins.Get(),
        });
    }

    private static async Task<IResult> GetConfig(HttpContext http, AgentConfigBuilder builder, CancellationToken ct)
    {
        var cfg = await builder.BuildAsync(http.User.GetAgentId(), ct);
        return cfg is null ? Results.NotFound() : Results.Ok(cfg);
    }

    private static async Task<IResult> PostResults(
        ResultsBatch batch, HttpContext http, ResultIngestService ingest, SentinelDbContext db, CancellationToken ct)
    {
        var agentId = http.User.GetAgentId();
        var accepted = await ingest.IngestAsync(agentId, batch.Results, ct);
        var configVersion = await db.Agents.Where(a => a.Id == agentId).Select(a => a.Host!.ConfigVersion).FirstAsync(ct);
        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, DateTimeOffset.UtcNow).SetProperty(a => a.IsOnline, true), ct);
        return Results.Ok(new ResultsAck { Accepted = accepted, ConfigVersion = configVersion, ServerTime = DateTimeOffset.UtcNow });
    }
}
