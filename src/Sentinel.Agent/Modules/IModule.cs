using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>Контекст выполнения проверки: конфигурация экземпляра и окружение агента.</summary>
public sealed class ModuleContext
{
    public ModuleContext(CheckConfig check, TimeSpan clockOffset, DateTimeOffset? clockOffsetMeasuredAt)
    {
        Check = check;
        ClockOffset = clockOffset;
        ClockOffsetMeasuredAt = clockOffsetMeasuredAt;
    }

    public CheckConfig Check { get; }
    /// <summary>Код клиента и имя хоста из конфигурации — для папок в облаке.</summary>
    public string TenantSlug { get; set; } = "";
    public string HostName { get; set; } = Environment.MachineName;
    /// <summary>Разница часов сервера и агента (server - local).</summary>
    public TimeSpan ClockOffset { get; }
    public DateTimeOffset? ClockOffsetMeasuredAt { get; }

    public T Settings<T>() where T : new()
    {
        if (Check.Settings.ValueKind == JsonValueKind.Undefined || Check.Settings.ValueKind == JsonValueKind.Null) return new T();
        return JsonSerializer.Deserialize<T>(Check.Settings.GetRawText(), SentinelJson.Options) ?? new T();
    }

    public CheckResult Result(string key, CheckStatus status, string summary, object? details = null)
    {
        return new CheckResult
        {
            CheckId = Check.Id,
            ModuleId = Check.ModuleId,
            Key = key,
            Status = status,
            Summary = summary,
            At = DateTimeOffset.UtcNow,
            Details = details is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(details, SentinelJson.Options),
        };
    }
}

/// <summary>Модуль проверки. Реализации не хранят состояние между вызовами, кроме кэшей.</summary>
public interface IModule
{
    string Id { get; }
    Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct);
}

/// <summary>Реестр доступных модулей по идентификатору.</summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IModule> _modules = new Dictionary<string, IModule>(StringComparer.OrdinalIgnoreCase);

    public ModuleRegistry(IEnumerable<IModule> modules)
    {
        foreach (var m in modules) _modules[m.Id] = m;
    }

    public IModule? Find(string id) => _modules.TryGetValue(id, out var m) ? m : null;

    public IEnumerable<string> Ids => _modules.Keys;
}
