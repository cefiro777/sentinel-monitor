using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Sentinel.Contracts.Protocol;

/// <summary>Полная конфигурация агента, выдаваемая сервером.</summary>
public sealed class AgentConfigDocument
{
    /// <summary>Монотонно растущая версия; агент перечитывает конфиг при изменении.</summary>
    public int Version { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }

    public int HeartbeatSeconds { get; set; } = 30;
    public int ResultsFlushSeconds { get; set; } = 15;

    /// <summary>Код клиента и имя хоста — для имён папок бэкапов в облаке.</summary>
    public string TenantSlug { get; set; } = "";
    public string HostName { get; set; } = "";

    public List<CheckConfig> Checks { get; set; } = new List<CheckConfig>();
}

/// <summary>Экземпляр модуля с настройками.</summary>
public sealed class CheckConfig
{
    public Guid Id { get; set; }
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>Настройки модуля по его JSON-схеме.</summary>
    public JsonElement Settings { get; set; }
}

/// <summary>Периодическое «я жив» по SignalR-каналу.</summary>
public sealed class HeartbeatMessage
{
    public DateTimeOffset At { get; set; }
    public string AgentVersion { get; set; } = "";
    public int ConfigVersion { get; set; }
    /// <summary>Сколько результатов ждёт отправки в офлайн-буфере.</summary>
    public int QueueDepth { get; set; }
    public double UptimeSeconds { get; set; }
}

/// <summary>Описание опубликованного дистрибутива агента. Подпись — Ed25519 сервера над строкой sha256 (hex, нижний регистр).</summary>
public sealed class AgentPackageManifest
{
    public string Version { get; set; } = "";
    public string File { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Signature { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
}
