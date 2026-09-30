namespace Sentinel.Server.Core.Entities;

/// <summary>
/// Шаблон набора проверок: «у всех серверов клиента следить за диском C:, службой X и бэкапом Y».
/// Применяется к любому числу хостов; проверки на хосте сопоставляются с шаблоном по паре модуль + название.
/// </summary>
public class CheckTemplate
{
    public Guid Id { get; set; }
    /// <summary>null — общий шаблон для всех клиентов.</summary>
    public Guid? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Список проверок (jsonb): массив объектов с полями moduleId, name, enabled, intervalSeconds, confirmCount, settings. Секреты в settings зашифрованы как у Check.</summary>
    public string ItemsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
