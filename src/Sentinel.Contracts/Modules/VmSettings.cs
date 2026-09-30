using System.Collections.Generic;

namespace Sentinel.Contracts.Modules;

/// <summary>
/// vm.hyperv — виртуальные машины на хосте Hyper-V (WMI root\virtualization\v2).
/// Модуль ставится на сам хост Hyper-V: агент видит все ВМ без дополнительных учёток.
/// </summary>
public sealed class HyperVModuleSettings
{
    /// <summary>Следить только за этими ВМ (имена как в диспетчере Hyper-V). Пусто — за всеми.</summary>
    public List<string> Include { get; set; } = new List<string>();
    /// <summary>Не следить за этими ВМ (тестовые, временные).</summary>
    public List<string> Exclude { get; set; } = new List<string>();
    /// <summary>ВМ, которые должны быть выключены штатно (резервные, шаблоны): выключенное состояние — норма.</summary>
    public List<string> ExpectedOff { get; set; } = new List<string>();
    /// <summary>Выключенная ВМ — критично (иначе предупреждение).</summary>
    public bool StoppedIsCritical { get; set; } = true;
    /// <summary>Предупреждать, если службы интеграции не отвечают (heartbeat).</summary>
    public bool CheckHeartbeat { get; set; } = true;
    /// <summary>Предупреждать о контрольных точках старше N дней (они съедают диск и тормозят ВМ). 0 — не проверять.</summary>
    public int CheckpointWarnDays { get; set; } = 3;
    /// <summary>Проверять состояние репликации Hyper-V, если она настроена.</summary>
    public bool CheckReplication { get; set; } = true;
}
