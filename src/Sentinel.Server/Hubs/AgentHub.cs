using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Sentinel.Contracts.Protocol;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Security;

namespace Sentinel.Server.Hubs;

/// <summary>Кто из агентов сейчас подключён и с каким connectionId.</summary>
public sealed class AgentConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, string> _connections = new();

    public void Connected(Guid agentId, string connectionId) => _connections[agentId] = connectionId;

    public void Disconnected(Guid agentId, string connectionId)
        => _connections.TryRemove(new KeyValuePair<Guid, string>(agentId, connectionId));

    public bool TryGet(Guid agentId, out string connectionId) => _connections.TryGetValue(agentId, out connectionId!);

    public bool IsConnected(Guid agentId) => _connections.ContainsKey(agentId);
}

[Authorize(AuthenticationSchemes = AgentAuthenticationHandler.Scheme)]
public sealed class AgentHub(AgentConnectionTracker tracker, SentinelDbContext db, Services.IncidentService incidents, Services.CommandService commands, ILogger<AgentHub> log) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var agentId = Context.User!.GetAgentId();
        tracker.Connected(agentId, Context.ConnectionId);
        await MarkOnlineAsync(agentId);
        log.LogInformation("Агент {AgentId} подключился ({ConnectionId})", agentId, Context.ConnectionId);
        var delivered = await commands.DeliverPendingAsync(agentId, Context.ConnectionAborted);
        if (delivered > 0) log.LogInformation("Агенту {AgentId} доставлено отложенных команд: {Count}", agentId, delivered);
    }

    /// <summary>Отмечает агента онлайн и закрывает инцидент «не на связи», если был.</summary>
    private async Task MarkOnlineAsync(Guid agentId, HeartbeatMessage? hb = null)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent is null) return;
        var wasOffline = !agent.IsOnline;
        agent.IsOnline = true;
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        if (hb is not null)
        {
            agent.AgentVersion = hb.AgentVersion;
            agent.ReportedConfigVersion = hb.ConfigVersion;
            agent.ReportedQueueDepth = hb.QueueDepth;
        }
        if (wasOffline) await incidents.OnAgentOnlineAsync(agent.HostId, default);
        await db.SaveChangesAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agentId = Context.User!.GetAgentId();
        tracker.Disconnected(agentId, Context.ConnectionId);
        if (!tracker.IsConnected(agentId))
        {
            await db.Agents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsOnline, false));
        }
        log.LogInformation("Агент {AgentId} отключился: {Reason}", agentId, exception?.Message ?? "штатно");
    }

    /// <summary>Агент → сервер: «я жив».</summary>
    public Task Heartbeat(HeartbeatMessage hb) => MarkOnlineAsync(Context.User!.GetAgentId(), hb);

    /// <summary>Агент → сервер: результат выполнения команды.</summary>
    public async Task CommandResult(CommandResult result)
    {
        var agentId = Context.User!.GetAgentId();
        var cmd = await db.Commands.FirstOrDefaultAsync(c => c.Id == result.CommandId && c.AgentId == agentId);
        if (cmd is null)
        {
            log.LogWarning("Результат неизвестной команды {CommandId} от агента {AgentId}", result.CommandId, agentId);
            return;
        }

        cmd.Status = result.Success ? CommandStatus.Succeeded : CommandStatus.Failed;
        cmd.Output = result.Output;
        cmd.Error = result.Error;
        cmd.StartedAt = result.StartedAt;
        cmd.FinishedAt = result.FinishedAt;
        db.AuditLog.Add(new AuditEntry
        {
            At = DateTimeOffset.UtcNow,
            AgentId = agentId,
            Action = "command.result",
            TargetType = nameof(Command),
            TargetId = cmd.Id,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { cmd.Type, result.Success, result.Error }),
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Отправка команд и уведомлений агентам через хаб.</summary>
public sealed class SignalRAgentMessenger(IHubContext<AgentHub> hub, AgentConnectionTracker tracker) : IAgentMessenger
{
    public bool IsConnected(Guid agentId) => tracker.IsConnected(agentId);

    public async Task<bool> SendCommandAsync(Guid agentId, CommandEnvelope envelope, CancellationToken ct = default)
    {
        if (!tracker.TryGet(agentId, out var connectionId)) return false;
        await hub.Clients.Client(connectionId).SendAsync(AgentHubContract.ExecuteCommand, envelope, ct);
        return true;
    }

    public async Task NotifyConfigChangedAsync(Guid agentId, int version, CancellationToken ct = default)
    {
        if (!tracker.TryGet(agentId, out var connectionId)) return;
        await hub.Clients.Client(connectionId).SendAsync(AgentHubContract.ConfigChanged, version, ct);
    }
}
