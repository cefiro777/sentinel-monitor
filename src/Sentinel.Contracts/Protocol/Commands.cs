using System;
using System.Text.Json;

namespace Sentinel.Contracts.Protocol;

/// <summary>Команда сервера агенту. Подписана приватным ключом сервера.</summary>
public sealed class CommandEnvelope
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    /// <summary>Тип: ping, config.refresh, service.restart, reboot, exec, ...</summary>
    public string Type { get; set; } = "";
    public JsonElement? Payload { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Ed25519-подпись (base64) канонической строки <see cref="CanonicalString"/>.</summary>
    public string Signature { get; set; } = "";

    /// <summary>Строка, которую подписывает сервер и проверяет агент.</summary>
    public string CanonicalString()
    {
        // Payload пересериализуется компактно: исходный текст может отличаться пробелами/переносами после передачи через SignalR.
        var payload = Payload.HasValue && Payload.Value.ValueKind != JsonValueKind.Null && Payload.Value.ValueKind != JsonValueKind.Undefined
            ? JsonSerializer.Serialize(Payload.Value)
            : "";
        return string.Join("\n",
            Id.ToString("D"),
            AgentId.ToString("D"),
            Type,
            IssuedAt.ToUnixTimeSeconds().ToString(),
            ExpiresAt.ToUnixTimeSeconds().ToString(),
            payload);
    }
}

public sealed class CommandResult
{
    public Guid CommandId { get; set; }
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
}

/// <summary>Известные типы команд. Строки, чтобы модули могли добавлять свои.</summary>
public static class CommandTypes
{
    public const string Ping = "ping";
    public const string ConfigRefresh = "config.refresh";
    public const string ServiceStart = "service.start";
    public const string ServiceStop = "service.stop";
    public const string ServiceRestart = "service.restart";
    public const string Reboot = "reboot";
    public const string Shutdown = "shutdown";
    public const string Exec = "exec";
    /// <summary>Внеочередной запуск задания бэкапа. Payload: { "checkId": "..." }</summary>
    public const string BackupRun = "backup.run";
    /// <summary>Проверить и установить обновление агента с сервера.</summary>
    public const string AgentUpdate = "agent.update";
}

/// <summary>Имена методов SignalR-хаба агентов, чтобы строки не расходились на двух сторонах.</summary>
public static class AgentHubContract
{
    public const string Path = "/hubs/agent";

    /// <summary>Агент → сервер.</summary>
    public const string Heartbeat = "Heartbeat";
    public const string CommandResult = "CommandResult";

    /// <summary>Сервер → агент.</summary>
    public const string ExecuteCommand = "ExecuteCommand";
    public const string ConfigChanged = "ConfigChanged";
}
