using System.Collections.Generic;

namespace Sentinel.Contracts.Modules;

/// <summary>Регистратор или камера с HTTP-API.</summary>
public sealed class CctvDevice
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;
    /// <summary>HTTPS вместо HTTP (сертификат регистратора обычно самоподписанный — принимается любой).</summary>
    public bool UseHttps { get; set; }
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    /// <summary>Недоступность устройства — критично (иначе предупреждение).</summary>
    public bool Critical { get; set; } = true;
    /// <summary>Проверять, что за последние N минут по каналам есть записи. 0 — не проверять.</summary>
    public int RecordingWindowMinutes { get; set; } = 30;
    /// <summary>Номера каналов для контроля записи. Пусто — все обнаруженные.</summary>
    public List<int> Channels { get; set; } = new List<int>();
    /// <summary>Каналы, которые не проверяются вовсе (нет камеры, снята на сезон): не попадают ни в доступность, ни в запись.</summary>
    public List<int> IgnoreChannels { get; set; } = new List<int>();
    /// <summary>Каналы с записью по движению/событию: отсутствие записи за окно — не проблема, проверяется только доступность.</summary>
    public List<int> MotionChannels { get; set; } = new List<int>();

    /// <summary>Ключ устройства в результатах и командах: host или host:port.</summary>
    public string Key => Port == 80 || Port == 0 ? Host.Trim() : $"{Host.Trim()}:{Port}";
}

/// <summary>cctv.hikvision — ISAPI: устройство, время, диски, каналы, наличие записи.</summary>
public sealed class HikvisionModuleSettings
{
    public List<CctvDevice> Devices { get; set; } = new List<CctvDevice>();
    public int TimeDriftWarnSeconds { get; set; } = 60;
    /// <summary>Предупреждение, если на диске регистратора свободно меньше N % (обычно перезапись — норма; 0 — не учитывать).</summary>
    public int HddFreeWarnPercent { get; set; } = 0;
    /// <summary>Выставлять время регистратора автоматически (по часам агента), если расхождение больше порога.</summary>
    public bool AutoSetTime { get; set; }
}

/// <summary>cctv.dahua — HTTP API Dahua/RVi: аналогично Hikvision.</summary>
public sealed class DahuaModuleSettings
{
    public List<CctvDevice> Devices { get; set; } = new List<CctvDevice>();
    public int TimeDriftWarnSeconds { get; set; } = 60;
    /// <summary>Выставлять время регистратора автоматически (по часам агента), если расхождение больше порога.</summary>
    public bool AutoSetTime { get; set; }
}

/// <summary>cctv.onvif — универсально для любых камер/регистраторов с ONVIF: доступность, время, профили, потоки.</summary>
public sealed class OnvifModuleSettings
{
    public List<CctvDevice> Devices { get; set; } = new List<CctvDevice>();
    public int TimeDriftWarnSeconds { get; set; } = 60;
    /// <summary>Проверять RTSP-поток каждого профиля (DESCRIBE + первые пакеты).</summary>
    public bool CheckStreams { get; set; } = true;
}

public sealed class RtspStream
{
    public string Name { get; set; } = "";
    /// <summary>rtsp://user:pass@host:554/Streaming/Channels/101</summary>
    public string Url { get; set; } = "";
    public bool Critical { get; set; } = true;
}

/// <summary>cctv.rtsp — живость конкретных потоков (регистраторы без API, IP-камеры).</summary>
public sealed class RtspModuleSettings
{
    public List<RtspStream> Streams { get; set; } = new List<RtspStream>();
    public int TimeoutMs { get; set; } = 8000;
    /// <summary>Дождаться реальных данных (RTP), а не только описания потока.</summary>
    public bool RequireData { get; set; } = true;
}

/// <summary>Состояние устройства видеонаблюдения — уходит в CheckResult.Details, отображается на странице «Видеонаблюдение».</summary>
public sealed class CctvDeviceInfo
{
    public string Vendor { get; set; } = "";
    public string? Model { get; set; }
    public string? Serial { get; set; }
    public string? Firmware { get; set; }
    public string? DeviceTime { get; set; }
    public double? TimeDriftSeconds { get; set; }
    public List<CctvHdd> Hdd { get; set; } = new List<CctvHdd>();
    public List<CctvChannel> Channels { get; set; } = new List<CctvChannel>();
    public List<string> Problems { get; set; } = new List<string>();
}

public sealed class CctvHdd
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public double CapacityGb { get; set; }
    public double FreeGb { get; set; }
}

public sealed class CctvChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>null — неизвестно (аналоговый канал без статуса).</summary>
    public bool? Online { get; set; }
    /// <summary>null — не проверялось.</summary>
    public bool? Recording { get; set; }
    public string? StreamUrl { get; set; }
    /// <summary>Режим по настройкам устройства: null — проверять полностью, "motion" — только доступность, "ignored" — не проверять.</summary>
    public string? Mode { get; set; }
    public bool? StreamOk { get; set; }
}
