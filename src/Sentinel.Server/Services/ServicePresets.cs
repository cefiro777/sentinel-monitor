using System.Reflection;
using System.Text.Json;

namespace Sentinel.Server.Services;

/// <summary>
/// Готовые наборы служб Windows по ролям сервера (Seed/service-presets.json, встроен в сборку).
/// Один источник для формы «Службы Windows» в дашборде и для встроенных шаблонов проверок.
/// </summary>
public sealed class ServicePresets
{
    public sealed record ServiceEntry(string Name, bool AutoRestart, bool Critical, string Comment);
    public sealed record Preset(string Id, string Title, string Description, List<ServiceEntry> Services);

    public IReadOnlyList<Preset> All { get; }

    public ServicePresets()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("service-presets.json")
            ?? throw new InvalidOperationException("Ресурс service-presets.json не найден в сборке.");
        All = JsonSerializer.Deserialize<List<Preset>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
    }

    public Preset? Get(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// Подсказки по частым событиям журналов Windows (Seed/event-hints.json): источник + коды → причина и что делать.
/// Сопоставление идёт в дашборде; пустой список кодов — подсказка для любого события этого источника.
/// </summary>
public sealed class EventHints
{
    public sealed record Hint(string Source, List<int> Ids, string Title, string Cause, string Fix);

    public IReadOnlyList<Hint> All { get; }

    public EventHints()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("event-hints.json")
            ?? throw new InvalidOperationException("Ресурс event-hints.json не найден в сборке.");
        All = JsonSerializer.Deserialize<List<Hint>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
    }
}
