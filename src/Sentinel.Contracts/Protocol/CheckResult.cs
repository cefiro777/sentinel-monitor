using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Sentinel.Contracts.Protocol;

/// <summary>Состояние проверки. Порядок важен: больше — хуже.</summary>
public enum CheckStatus
{
    Ok = 0,
    Warning = 1,
    Critical = 2,
    /// <summary>Проверку выполнить не удалось (нет доступа, ошибка модуля).</summary>
    Unknown = 3,
}

/// <summary>
/// Результат одной проверки. Один экземпляр модуля (<see cref="CheckId"/>) может
/// порождать несколько результатов с разными <see cref="Key"/> (служба, диск, хост в сети).
/// </summary>
public sealed class CheckResult
{
    /// <summary>Идентификатор экземпляра модуля из конфигурации агента.</summary>
    public Guid CheckId { get; set; }

    /// <summary>Идентификатор модуля (services, disks, ...).</summary>
    public string ModuleId { get; set; } = "";

    /// <summary>Подключ внутри проверки: имя службы, буква диска, адрес хоста. Пусто — единичная проверка.</summary>
    public string Key { get; set; } = "";

    public CheckStatus Status { get; set; }

    /// <summary>Короткое человекочитаемое описание: «Свободно 12 ГБ (8%)».</summary>
    public string Summary { get; set; } = "";

    /// <summary>Произвольные детали модуля для отображения в дашборде.</summary>
    public JsonElement? Details { get; set; }

    /// <summary>Момент выполнения проверки (UTC, по часам агента).</summary>
    public DateTimeOffset At { get; set; }

    public List<MetricSample> Metrics { get; set; } = new List<MetricSample>();
}

/// <summary>Числовое значение для графиков.</summary>
public sealed class MetricSample
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public string? Unit { get; set; }
}

/// <summary>Пакет результатов, отправляемый агентом (в т.ч. накопленный офлайн).</summary>
public sealed class ResultsBatch
{
    public Guid AgentId { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public List<CheckResult> Results { get; set; } = new List<CheckResult>();
}

public sealed class ResultsAck
{
    public int Accepted { get; set; }
    /// <summary>Версия конфигурации на сервере — агент сравнит со своей и при необходимости перечитает.</summary>
    public int ConfigVersion { get; set; }
    /// <summary>Время сервера — агент использует для оценки дрейфа часов.</summary>
    public DateTimeOffset ServerTime { get; set; }
}
