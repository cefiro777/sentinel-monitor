using System.Text.Json;
using Sentinel.Server.Core.Entities;

namespace Sentinel.Server.Services;

/// <summary>
/// Встроенные шаблоны проверок по ролям сервера. Создаются один раз, пока таблица шаблонов пуста;
/// дальше это обычные шаблоны — их можно править и удалять.
/// </summary>
public static class BuiltInTemplates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Note = "Встроенный шаблон — можно менять и удалять.";

    public static IEnumerable<CheckTemplate> Create(ServicePresets presets, DateTimeOffset now)
    {
        // Базовый набор — общий для всех ролей.
        var baseItems = new List<object>
        {
            Item("system", "Система", 60, new { cpuWarnPercent = 85, cpuCritPercent = 95, memoryWarnPercent = 85, memoryCritPercent = 95, clockDriftWarnSeconds = 60, warnOnPendingReboot = true }),
            Item("disks", "Диски", 300, new { warnFreePercent = 15, critFreePercent = 7, warnFreeGb = 0, critFreeGb = 0, include = Array.Empty<string>(), exclude = Array.Empty<string>() }),
            Item("eventlog", "Журнал событий", 300, new { logs = new[] { "System", "Application" }, levels = new[] { 1, 2 }, lookbackMinutes = 0, includeSources = Array.Empty<string>(), excludeSources = Array.Empty<string>(), includeEventIds = Array.Empty<int>(), excludeEventIds = Array.Empty<int>(), warnCount = 1, critCount = 10, maxEventsInDetails = 30 }),
            Services(presets, "base", "Службы Windows"),
        };

        yield return Template("Базовый сервер", "Система, диски, журнал событий и базовые службы Windows — для любого сервера. " + Note, baseItems);

        foreach (var (title, description, roles) in new[]
        {
            ("Контроллер домена", "Базовый набор + службы AD DS (NTDS, Kerberos, DNS, репликация SYSVOL).", new[] { "ad-ds" }),
            ("Терминальный сервер", "Базовый набор + Netlogon + службы RDS Session Host.", new[] { "domain-member", "rds" }),
            ("MS SQL Server", "Базовый набор + Netlogon + MSSQLSERVER, агент SQL и VSS-модуль. Для Express уберите SQLSERVERAGENT.", new[] { "domain-member", "mssql" }),
            ("Сервер 1С:Предприятие", "Базовый набор + Netlogon + агент сервера 1С. Проверьте порт в имени службы.", new[] { "domain-member", "1c" }),
            ("Файловый сервер и печать", "Базовый набор + Netlogon + диспетчер печати.", new[] { "domain-member", "print" }),
            ("Hyper-V", "Базовый набор + служба управления виртуальными машинами и контроль самих ВМ.", new[] { "hyperv" }),
        })
        {
            var items = new List<object>(baseItems);
            foreach (var role in roles)
            {
                var p = presets.Get(role);
                if (p is not null) items.Add(Services(presets, role, "Службы: " + p.Title));
            }
            if (roles.Contains("hyperv"))
                items.Add(Item("vm.hyperv", "Виртуальные машины", 300, new { include = Array.Empty<string>(), exclude = Array.Empty<string>(), expectedOff = Array.Empty<string>(), stoppedIsCritical = true, checkHeartbeat = true, checkpointWarnDays = 3, checkReplication = true }));
            yield return Template(title, description + " " + Note, items);
        }

        CheckTemplate Template(string name, string description, List<object> items) => new()
        {
            Id = Guid.NewGuid(), TenantId = null, Name = name, Description = description,
            ItemsJson = JsonSerializer.Serialize(items, Json), CreatedAt = now, UpdatedAt = now,
        };
    }

    private static object Item(string moduleId, string name, int interval, object settings)
        => new { moduleId, name, enabled = true, intervalSeconds = interval, confirmCount = 2, settings };

    private static object Services(ServicePresets presets, string presetId, string name)
    {
        var p = presets.Get(presetId) ?? throw new InvalidOperationException($"Нет набора служб «{presetId}»");
        return Item("services", name, 60, new { services = p.Services.Select(s => new { name = s.Name, autoRestart = s.AutoRestart, critical = s.Critical, comment = s.Comment }).ToList() });
    }
}
