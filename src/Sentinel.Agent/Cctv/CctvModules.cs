using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Cctv;

internal static class CctvResult
{
    /// <summary>
    /// Автоподвод часов: если расхождение больше порога — выставляем время агента и отражаем это в результате.
    /// Сам дрейф в результате оставляем (чтобы был виден в истории), но проблемой его уже не считаем.
    /// </summary>
    public static async Task AutoSetTimeAsync(ModuleContext ctx, CctvDevice d, CctvDeviceInfo info, int driftWarnSeconds, Func<DateTimeOffset, CancellationToken, Task> setTime, CancellationToken ct)
    {
        if (info.TimeDriftSeconds is not double drift || Math.Abs(drift) <= driftWarnSeconds) return;
        // Эталон — часы сервера мониторинга (свой офсет агент знает), а не локальные часы: они тоже могут врать.
        var now = DateTimeOffset.Now + ctx.ClockOffset;
        try
        {
            await setTime(now, ct);
            info.Problems.Add($"часы расходились на {drift:+0;-0} с — время выставлено автоматически");
            info.TimeDriftSeconds = 0;
        }
        catch (Exception ex) { info.Problems.Add($"часы расходятся на {drift:+0;-0} с, выставить автоматически не удалось: {ex.Message}"); }
    }

    /// <summary>Сводит найденные проблемы устройства в статус и короткое описание.</summary>
    public static CheckResult Build(ModuleContext ctx, CctvDevice d, CctvDeviceInfo info, int driftWarnSeconds)
    {
        var key = d.Key;
        var label = string.IsNullOrWhiteSpace(d.Name) ? d.Key : d.Name;
        var critical = new List<string>();
        var warnings = new List<string>(info.Problems);

        if (info.TimeDriftSeconds is double drift && Math.Abs(drift) > driftWarnSeconds)
            warnings.Add($"часы расходятся на {drift:+0;-0} с");

        // Нет записи или диск не в порядке — критично: ради этого регистратор и стоит.
        foreach (var p in info.Problems.ToList())
            if (p.Contains("нет записи") || p.StartsWith("диск") || p.Contains("нет ни одного диска")) { critical.Add(p); warnings.Remove(p); }

        var watched = info.Channels.Where(c => c.Mode != "ignored").ToList();
        var channels = watched.Count;
        var online = watched.Count(c => c.Online != false);
        var recording = watched.Count(c => c.Recording == true);
        var parts = new List<string>();
        if (channels > 0) parts.Add(watched.Any(c => c.Online is not null) ? $"каналов онлайн {online}/{channels}" : $"каналов {channels}");
        if (watched.Any(c => c.Recording is not null)) parts.Add($"пишут {recording}/{watched.Count(c => c.Recording is not null)}");
        if (info.Channels.Count > channels) parts.Add($"игнорируется {info.Channels.Count - channels}");
        if (info.Hdd.Count > 0) parts.Add($"HDD {string.Join(",", info.Hdd.Select(h => h.Status))}");
        if (info.TimeDriftSeconds is double dd) parts.Add($"часы {dd:+0;-0} с");

        CheckStatus status;
        string summary;
        if (critical.Count > 0) { status = d.Critical ? CheckStatus.Critical : CheckStatus.Warning; summary = $"{label}: {string.Join("; ", critical)}"; }
        else if (warnings.Count > 0) { status = CheckStatus.Warning; summary = $"{label}: {string.Join("; ", warnings)}"; }
        else { status = CheckStatus.Ok; summary = $"{label}: в порядке ({string.Join(", ", parts)})"; }
        if (status != CheckStatus.Ok && parts.Count > 0) summary += $" [{string.Join(", ", parts)}]";

        var r = ctx.Result(key, status, summary, info);
        if (info.TimeDriftSeconds is double m) r.Metrics.Add(new MetricSample { Name = "cctv_time_drift_s", Value = m, Unit = "s" });
        r.Metrics.Add(new MetricSample { Name = "cctv_channels_online", Value = online });
        return r;
    }

    public static CheckResult Unreachable(ModuleContext ctx, CctvDevice d, string error)
    {
        var label = string.IsNullOrWhiteSpace(d.Name) ? d.Key : d.Name;
        // 8000 у Hikvision и 37777 у Dahua — бинарные SDK-порты, HTTP-API там не отвечает.
        if (d.Port == 8000 || d.Port == 37777) error += $" (порт {d.Port} — это SDK-порт; для HTTP-API нужен порт веб-интерфейса, обычно 80)";
        return ctx.Result(d.Key, d.Critical ? CheckStatus.Critical : CheckStatus.Warning, $"{label}: недоступен — {error}");
    }
}

public sealed class HikvisionModule : IModule
{
    public string Id => ModuleIds.CctvHikvision;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<HikvisionModuleSettings>();
        if (s.Devices.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного устройства") };
        var results = new List<CheckResult>();
        foreach (var d in s.Devices.Where(d => !string.IsNullOrWhiteSpace(d.Host)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using (var client = new HikvisionClient(d, TimeSpan.FromSeconds(20)))
                {
                    var info = await client.InspectAsync(d.RecordingWindowMinutes, ct);
                    if (s.AutoSetTime) await CctvResult.AutoSetTimeAsync(ctx, d, info, s.TimeDriftWarnSeconds, client.SetTimeAsync, ct);
                    results.Add(CctvResult.Build(ctx, d, info, s.TimeDriftWarnSeconds));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { results.Add(CctvResult.Unreachable(ctx, d, ex.InnerException?.Message ?? ex.Message)); }
        }
        return results;
    }
}

public sealed class DahuaModule : IModule
{
    public string Id => ModuleIds.CctvDahua;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<DahuaModuleSettings>();
        if (s.Devices.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного устройства") };
        var results = new List<CheckResult>();
        foreach (var d in s.Devices.Where(d => !string.IsNullOrWhiteSpace(d.Host)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using (var client = new DahuaClient(d, TimeSpan.FromSeconds(20)))
                {
                    var info = await client.InspectAsync(d.RecordingWindowMinutes, ct);
                    if (s.AutoSetTime) await CctvResult.AutoSetTimeAsync(ctx, d, info, s.TimeDriftWarnSeconds, client.SetTimeAsync, ct);
                    results.Add(CctvResult.Build(ctx, d, info, s.TimeDriftWarnSeconds));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { results.Add(CctvResult.Unreachable(ctx, d, ex.InnerException?.Message ?? ex.Message)); }
        }
        return results;
    }
}

/// <summary>Универсальный ONVIF: время, модель, профили и живость их RTSP-потоков.</summary>
public sealed class OnvifModule : IModule
{
    public string Id => ModuleIds.CctvOnvif;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<OnvifModuleSettings>();
        if (s.Devices.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного устройства") };
        var results = new List<CheckResult>();
        foreach (var d in s.Devices.Where(d => !string.IsNullOrWhiteSpace(d.Host)))
        {
            ct.ThrowIfCancellationRequested();
            var info = new CctvDeviceInfo { Vendor = "ONVIF" };
            try
            {
                using (var onvif = new OnvifClient(d.Host, d.Port, d.Username, d.Password, TimeSpan.FromSeconds(15)))
                {
                    var time = await onvif.GetSystemDateAndTimeAsync(ct);
                    info.DeviceTime = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
                    info.TimeDriftSeconds = Math.Round((time - DateTimeOffset.UtcNow).TotalSeconds);

                    try
                    {
                        var dev = await onvif.GetDeviceInformationAsync(ct);
                        info.Vendor = string.IsNullOrEmpty(dev.Manufacturer) ? "ONVIF" : dev.Manufacturer!;
                        info.Model = dev.Model; info.Firmware = dev.Firmware; info.Serial = dev.Serial;
                    }
                    catch (Exception ex) { info.Problems.Add(ex.Message); }

                    try
                    {
                        var profiles = await onvif.GetProfilesAsync(s.CheckStreams, ct);
                        var n = 0;
                        foreach (var p in profiles)
                        {
                            var ch = new CctvChannel { Id = ++n, Name = p.Name, StreamUrl = p.StreamUri };
                            if (s.CheckStreams && p.StreamUri is not null)
                            {
                                var url = InjectCredentials(p.StreamUri, d.Username, d.Password);
                                var probe = await RtspProbe.ProbeAsync(url, requireData: true, TimeSpan.FromSeconds(10), ct);
                                ch.StreamOk = probe.DataReceived;
                                ch.Online = probe.DescribeOk;
                                if (!probe.DataReceived) info.Problems.Add($"поток «{p.Name}»: {probe.Error}");
                            }
                            info.Channels.Add(ch);
                        }
                        if (profiles.Count == 0) info.Problems.Add("нет медиапрофилей");
                    }
                    catch (Exception ex) { info.Problems.Add("профили: " + ex.Message); }
                }
                results.Add(CctvResult.Build(ctx, d, info, s.TimeDriftWarnSeconds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { results.Add(CctvResult.Unreachable(ctx, d, ex.InnerException?.Message ?? ex.Message)); }
        }
        return results;
    }

    private static string InjectCredentials(string rtsp, string user, string pass)
    {
        try
        {
            var b = new UriBuilder(rtsp);
            if (string.IsNullOrEmpty(b.UserName) && !string.IsNullOrEmpty(user)) { b.UserName = Uri.EscapeDataString(user); b.Password = Uri.EscapeDataString(pass); }
            return b.Uri.ToString();
        }
        catch { return rtsp; }
    }
}

/// <summary>Живость конкретных RTSP-потоков.</summary>
public sealed class RtspModule : IModule
{
    public string Id => ModuleIds.CctvRtsp;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<RtspModuleSettings>();
        if (s.Streams.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного потока") };
        var tasks = s.Streams.Where(x => !string.IsNullOrWhiteSpace(x.Url)).Select(async st =>
        {
            var probe = await RtspProbe.ProbeAsync(st.Url, s.RequireData, TimeSpan.FromMilliseconds(Math.Max(2000, s.TimeoutMs)), ct);
            var key = SafeKey(st.Url);
            var label = string.IsNullOrWhiteSpace(st.Name) ? key : st.Name;
            var ok = s.RequireData ? probe.DataReceived : probe.DescribeOk;
            var details = new { probe.DescribeOk, probe.DataReceived, probe.Codec, probe.Tracks, latencyMs = Math.Round(probe.LatencyMs), url = key };
            if (ok)
            {
                var r = ctx.Result(key, CheckStatus.Ok, $"{label}: поток идёт ({probe.Codec ?? "?"}, {probe.LatencyMs:0} мс)", details);
                r.Metrics.Add(new MetricSample { Name = "rtsp_describe_ms", Value = Math.Round(probe.LatencyMs), Unit = "ms" });
                return r;
            }
            return ctx.Result(key, st.Critical ? CheckStatus.Critical : CheckStatus.Warning, $"{label}: {probe.Error ?? "нет данных"}", details);
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>URL без логина/пароля — в ключ и детали секреты не попадают.</summary>
    private static string SafeKey(string url)
    {
        try { var b = new UriBuilder(url) { UserName = "", Password = "" }; return b.Uri.ToString(); }
        catch { return url; }
    }
}

/// <summary>cctv.settime / cctv.reboot — по устройству из настроек проверки. Payload: { "checkId": "...", "host": "192.168.1.100" }</summary>
public sealed class CctvCommandHandler : Commands.ICommandHandler
{
    private readonly Func<AgentConfigDocument> _config;
    private readonly ILogger _log;
    public CctvCommandHandler(Func<AgentConfigDocument> config, ILogger log) { _config = config; _log = log; }

    public string Type => "cctv.*";

    public async Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct)
    {
        if (cmd.Payload?.TryGetProperty("checkId", out var idEl) != true || !Guid.TryParse(idEl.GetString(), out var checkId)) throw new ArgumentException("Нужен payload.checkId.");
        var host = cmd.Payload?.TryGetProperty("host", out var h) == true ? h.GetString() : null;
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Нужен payload.host.");

        var check = _config().Checks.FirstOrDefault(c => c.Id == checkId) ?? throw new InvalidOperationException("Проверка не найдена в конфигурации агента.");
        var ctx = new ModuleContext(check, TimeSpan.Zero, null);
        CctvDevice device;
        string vendor;
        switch (check.ModuleId)
        {
            case ModuleIds.CctvHikvision: device = Find(ctx.Settings<HikvisionModuleSettings>().Devices, host!); vendor = "hikvision"; break;
            case ModuleIds.CctvDahua: device = Find(ctx.Settings<DahuaModuleSettings>().Devices, host!); vendor = "dahua"; break;
            default: throw new NotSupportedException($"Команды для модуля {check.ModuleId} не поддерживаются (только Hikvision и Dahua).");
        }

        var action = cmd.Type.ToLowerInvariant();
        _log.LogWarning("CCTV {Action} → {Vendor} {Host}", action, vendor, device.Host);
        if (vendor == "hikvision")
        {
            using (var c = new HikvisionClient(device, TimeSpan.FromSeconds(20)))
            {
                if (action == "cctv.settime") { await c.SetTimeAsync(ct); return $"{device.Host}: время установлено {DateTime.Now:dd.MM.yyyy HH:mm:ss}"; }
                if (action == "cctv.reboot") { await c.RebootAsync(ct); return $"{device.Host}: перезагрузка запущена"; }
            }
        }
        else
        {
            using (var c = new DahuaClient(device, TimeSpan.FromSeconds(20)))
            {
                if (action == "cctv.settime") { await c.SetTimeAsync(ct); return $"{device.Host}: время установлено {DateTime.Now:dd.MM.yyyy HH:mm:ss}"; }
                if (action == "cctv.reboot") { await c.RebootAsync(ct); return $"{device.Host}: перезагрузка запущена"; }
            }
        }
        throw new NotSupportedException("Неизвестная команда: " + cmd.Type);
    }

    private static CctvDevice Find(List<CctvDevice> devices, string host)
        => devices.FirstOrDefault(d => d.Key.Equals(host, StringComparison.OrdinalIgnoreCase) || d.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Устройство {host} не найдено в настройках проверки.");
}
