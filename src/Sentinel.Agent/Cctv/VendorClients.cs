using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Sentinel.Contracts.Modules;

namespace Sentinel.Agent.Cctv;

/// <summary>HTTP с Digest/Basic-авторизацией (HttpClientHandler на .NET Framework умеет Digest сам).</summary>
public abstract class VendorClientBase : IDisposable
{
    protected readonly HttpClient Http;
    protected readonly string Base;
    protected readonly CctvDevice Device;

    protected VendorClientBase(CctvDevice device, TimeSpan timeout)
    {
        Device = device;
        Base = $"{(device.UseHttps ? "https" : "http")}://{device.Host}:{device.Port}";
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(device.Username, device.Password),
            PreAuthenticate = true,
            AllowAutoRedirect = false,
        };
        // У регистраторов самоподписанные сертификаты на IP — проверять их нечем; защита здесь от прослушки, не от подмены.
        if (device.UseHttps) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        Http = new HttpClient(handler) { Timeout = timeout };
    }

    protected async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using (var resp = await Http.GetAsync(Base + path, ct))
        {
            if ((int)resp.StatusCode == 401) throw new InvalidOperationException("неверный логин/пароль (401)");
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {path}");
            return await ReadUtf8Async(resp);
        }
    }

    protected async Task<string> SendAsync(HttpMethod method, string path, string? body, string contentType, CancellationToken ct)
    {
        using (var req = new HttpRequestMessage(method, Base + path))
        {
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, contentType);
            using (var resp = await Http.SendAsync(req, ct))
            {
                var text = await ReadUtf8Async(resp);
                if ((int)resp.StatusCode == 401) throw new InvalidOperationException("неверный логин/пароль (401)");
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {path}: {Truncate(text)}");
                return text;
            }
        }
    }

    /// <summary>Регистраторы шлют Content-Type с charset="UTF-8" в кавычках — ReadAsStringAsync на .NET Framework на этом падает.</summary>
    protected static async Task<string> ReadUtf8Async(HttpResponseMessage resp)
        => Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());

    protected static string Truncate(string s) => s.Length > 200 ? s.Substring(0, 200) : s;
    public void Dispose() => Http.Dispose();
}

/// <summary>Hikvision / HiWatch ISAPI.</summary>
public sealed class HikvisionClient : VendorClientBase
{
    public HikvisionClient(CctvDevice device, TimeSpan timeout) : base(device, timeout) { }

    public async Task<CctvDeviceInfo> InspectAsync(int recordingWindowMinutes, CancellationToken ct)
    {
        var info = new CctvDeviceInfo { Vendor = "Hikvision" };

        var dev = XDocument.Parse(await GetStringAsync("/ISAPI/System/deviceInfo", ct)).Root!;
        info.Model = dev.Elements().FirstOrDefaultLocal("model")?.Value;
        info.Serial = dev.Elements().FirstOrDefaultLocal("serialNumber")?.Value;
        info.Firmware = (dev.Elements().FirstOrDefaultLocal("firmwareVersion")?.Value + " " + dev.Elements().FirstOrDefaultLocal("firmwareReleasedDate")?.Value).Trim();

        // Время: <localTime>2026-09-17T18:00:00+07:00</localTime>
        try
        {
            var time = XDocument.Parse(await GetStringAsync("/ISAPI/System/time", ct)).Root!;
            var local = time.Elements().FirstOrDefaultLocal("localTime")?.Value;
            if (local is not null && DateTimeOffset.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                info.DeviceTime = t.ToString("yyyy-MM-dd HH:mm:ss zzz");
                info.TimeDriftSeconds = Math.Round((t - DateTimeOffset.UtcNow).TotalSeconds);
            }
        }
        catch (Exception ex) { info.Problems.Add("время: " + ex.Message); }

        // Диски
        try
        {
            var storage = XDocument.Parse(await GetStringAsync("/ISAPI/ContentMgmt/Storage", ct)).Root!;
            foreach (var hdd in storage.Descendants().WhereLocal("hdd"))
            {
                var status = hdd.Elements().FirstOrDefaultLocal("status")?.Value ?? "?";
                var cap = double.TryParse(hdd.Elements().FirstOrDefaultLocal("capacity")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var c) ? c / 1024 : 0;
                var free = double.TryParse(hdd.Elements().FirstOrDefaultLocal("freeSpace")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? f / 1024 : 0;
                info.Hdd.Add(new CctvHdd { Name = hdd.Elements().FirstOrDefaultLocal("hddName")?.Value ?? "HDD", Status = status, CapacityGb = Math.Round(cap, 1), FreeGb = Math.Round(free, 1) });
                if (status != "ok") info.Problems.Add($"диск {hdd.Elements().FirstOrDefaultLocal("hddName")?.Value}: {status}");
            }
            if (info.Hdd.Count == 0) info.Problems.Add("нет ни одного диска");
        }
        catch (Exception ex) { info.Problems.Add("диски: " + ex.Message); }

        // Каналы: IP (NVR и IP-каналы гибридных DVR) со статусом online + аналоговые входы (DVR) без статуса.
        // Оба списка нужны всегда: у гибридного DS-H2xx с одной IP-камерой аналоговые 1–16 иначе теряются.
        var ipError = (string?)null;
        try
        {
            var proxy = XDocument.Parse(await GetStringAsync("/ISAPI/ContentMgmt/InputProxy/channels/status", ct)).Root!;
            foreach (var ch in proxy.Elements().WhereLocal("InputProxyChannelStatus"))
            {
                var id = int.Parse(ch.Elements().FirstOrDefaultLocal("id")?.Value ?? "0");
                var online = ch.Elements().FirstOrDefaultLocal("online")?.Value == "true";
                info.Channels.Add(new CctvChannel { Id = id, Name = ch.Elements().FirstOrDefaultLocal("sourceInputPortDescriptor")?.Elements().FirstOrDefaultLocal("ipAddress")?.Value ?? $"IP {id}", Online = online });
            }
        }
        catch (Exception ex) { ipError = ex.Message; /* не NVR — нормально */ }
        try
        {
            var inputs = XDocument.Parse(await GetStringAsync("/ISAPI/System/Video/inputs/channels", ct)).Root!;
            foreach (var ch in inputs.Elements().WhereLocal("VideoInputChannel"))
            {
                var id = int.Parse(ch.Elements().FirstOrDefaultLocal("id")?.Value ?? "0");
                var enabled = ch.Elements().FirstOrDefaultLocal("videoInputEnabled")?.Value != "false";
                // На чистых NVR этот же список может дублировать IP-каналы — по id не дублируем.
                if (enabled && info.Channels.All(c => c.Id != id))
                    info.Channels.Add(new CctvChannel { Id = id, Name = ch.Elements().FirstOrDefaultLocal("name")?.Value ?? $"Канал {id}" });
            }
        }
        catch (Exception ex) { if (info.Channels.Count == 0) info.Problems.Add("каналы: " + (ipError is null ? ex.Message : $"{ex.Message}; IP-каналы: {ipError}")); }
        ChannelRules.Apply(info, Device);

        // Запись: ищем записи за окно по каждому каналу (trackID = канал*100+1)
        if (recordingWindowMinutes > 0)
        {
            var to = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(info.TimeDriftSeconds ?? 0);
            var from = to.AddMinutes(-recordingWindowMinutes);
            foreach (var ch in ChannelRules.ForRecording(info, Device))
            {
                try
                {
                    var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?><CMSearchDescription><searchID>" + Guid.NewGuid().ToString().ToUpperInvariant() + "</searchID>" +
                               $"<trackIDList><trackID>{ch.Id * 100 + 1}</trackID></trackIDList>" +
                               $"<timeSpanList><timeSpan><startTime>{from.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}</startTime><endTime>{to.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}</endTime></timeSpan></timeSpanList>" +
                               "<maxResults>1</maxResults><searchResultPostion>0</searchResultPostion><metadataList><metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor></metadataList></CMSearchDescription>";
                    var result = XDocument.Parse(await SendAsync(HttpMethod.Post, "/ISAPI/ContentMgmt/search", body, "application/xml", ct)).Root!;
                    var matches = int.TryParse(result.Elements().FirstOrDefaultLocal("numOfMatches")?.Value, out var n) ? n : 0;
                    ch.Recording = matches > 0;
                    if (!ch.Recording.Value) info.Problems.Add($"канал {ch.Id}: нет записи за {recordingWindowMinutes} мин");
                }
                catch (Exception ex) { info.Problems.Add($"канал {ch.Id}: поиск записи — {ex.Message}"); }
            }
        }
        return info;
    }

    public Task SetTimeAsync(CancellationToken ct) => SetTimeAsync(DateTimeOffset.Now, ct);

    public async Task SetTimeAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Формат Hikvision: CST-7:00:00 для UTC+7 (знак инвертирован, как в POSIX TZ).
        var off = now.Offset;
        var tz = $"CST{(off >= TimeSpan.Zero ? "-" : "+")}{Math.Abs(off.Hours)}:{Math.Abs(off.Minutes):00}:00";
        var body = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Time><timeMode>manual</timeMode><localTime>{now:yyyy-MM-dd'T'HH:mm:sszzz}</localTime><timeZone>{tz}</timeZone></Time>";
        await SendAsync(HttpMethod.Put, "/ISAPI/System/time", body, "application/xml", ct);
    }

    public Task RebootAsync(CancellationToken ct) => SendAsync(HttpMethod.Put, "/ISAPI/System/reboot", null, "application/xml", ct);
}

/// <summary>Dahua / RVi HTTP API (cgi-bin).</summary>
public sealed class DahuaClient : VendorClientBase
{
    public DahuaClient(CctvDevice device, TimeSpan timeout) : base(device, timeout) { }

    private static Dictionary<string, string> Kv(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf('=');
            if (idx > 0) d[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
        }
        return d;
    }

    public async Task<CctvDeviceInfo> InspectAsync(int recordingWindowMinutes, CancellationToken ct)
    {
        var info = new CctvDeviceInfo { Vendor = "Dahua" };
        var sys = Kv(await GetStringAsync("/cgi-bin/magicBox.cgi?action=getSystemInfo", ct));
        info.Serial = sys.TryGetValue("serialNumber", out var sn) ? sn : null;
        try { info.Model = Kv(await GetStringAsync("/cgi-bin/magicBox.cgi?action=getDeviceType", ct)).TryGetValue("type", out var t) ? t : null; } catch { }
        try { info.Firmware = Kv(await GetStringAsync("/cgi-bin/magicBox.cgi?action=getSoftwareVersion", ct)).TryGetValue("version", out var v) ? v : null; } catch { }

        // Время: result=2026-09-17 18:00:00 (локальное время устройства; считаем, что зона совпадает с агентом)
        try
        {
            var t = Kv(await GetStringAsync("/cgi-bin/global.cgi?action=getCurrentTime", ct));
            if (t.TryGetValue("result", out var s) && DateTime.TryParseExact(s, "yyyy-M-d H:m:s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                var dto = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
                info.DeviceTime = dto.ToString("yyyy-MM-dd HH:mm:ss zzz");
                info.TimeDriftSeconds = Math.Round((dto - DateTimeOffset.Now).TotalSeconds);
            }
        }
        catch (Exception ex) { info.Problems.Add("время: " + ex.Message); }

        // Диски: list[0].State=OK, list[0].Detail[0].TotalBytes / IsError
        try
        {
            var st = Kv(await GetStringAsync("/cgi-bin/storageDevice.cgi?action=getDeviceAllInfo", ct));
            var names = st.Keys.Where(k => Regex.IsMatch(k, @"^list\[\d+\]\.Name$")).ToList();
            foreach (var key in names)
            {
                var idx = Regex.Match(key, @"\[(\d+)\]").Groups[1].Value;
                var state = st.TryGetValue($"list[{idx}].State", out var s) ? s : "?";
                double total = 0, used = 0;
                if (st.TryGetValue($"list[{idx}].Detail[0].TotalBytes", out var tb)) double.TryParse(tb, NumberStyles.Any, CultureInfo.InvariantCulture, out total);
                if (st.TryGetValue($"list[{idx}].Detail[0].UsedBytes", out var ub)) double.TryParse(ub, NumberStyles.Any, CultureInfo.InvariantCulture, out used);
                var hdd = new CctvHdd { Name = st[key], Status = state, CapacityGb = Math.Round(total / 1073741824, 1), FreeGb = Math.Round((total - used) / 1073741824, 1) };
                info.Hdd.Add(hdd);
                if (!state.Equals("OK", StringComparison.OrdinalIgnoreCase)) info.Problems.Add($"диск {hdd.Name}: {state}");
            }
            if (info.Hdd.Count == 0) info.Problems.Add("нет ни одного диска");
        }
        catch (Exception ex) { info.Problems.Add("диски: " + ex.Message); }

        // Каналы NVR: getCameraState → states[i].channel / connectionState
        try
        {
            var cs = Kv(await GetStringAsync("/cgi-bin/LogicDeviceManager.cgi?action=getCameraState&uniqueChannels[0]=-1", ct));
            foreach (var key in cs.Keys.Where(k => Regex.IsMatch(k, @"^states\[\d+\]\.channel$")).ToList())
            {
                var idx = Regex.Match(key, @"\[(\d+)\]").Groups[1].Value;
                var chan = int.Parse(cs[key]);
                var online = cs.TryGetValue($"states[{idx}].connectionState", out var c) && c.Equals("Connected", StringComparison.OrdinalIgnoreCase);
                info.Channels.Add(new CctvChannel { Id = chan + 1, Name = $"Канал {chan + 1}", Online = online });
            }
        }
        catch { /* DVR без IP-каналов */ }
        // Названия каналов — для всех (и аналоговых гибридного DVR, которых нет в getCameraState).
        try
        {
            var titles = Kv(await GetStringAsync("/cgi-bin/configManager.cgi?action=getConfig&name=ChannelTitle", ct));
            foreach (var key in titles.Keys.Where(k => Regex.IsMatch(k, @"^table\.ChannelTitle\[\d+\]\.Name$")).ToList())
            {
                var id = int.Parse(Regex.Match(key, @"\[(\d+)\]").Groups[1].Value) + 1;
                var existing = info.Channels.FirstOrDefault(c => c.Id == id);
                if (existing is null) info.Channels.Add(new CctvChannel { Id = id, Name = titles[key] });
                else if (!string.IsNullOrWhiteSpace(titles[key])) existing.Name = titles[key];
            }
        }
        catch (Exception ex) { if (info.Channels.Count == 0) info.Problems.Add("каналы: " + ex.Message); }
        ChannelRules.Apply(info, Device);

        // Запись за окно: mediaFileFind (factory.create → findFile → findNextFile → destroy)
        if (recordingWindowMinutes > 0)
        {
            var to = DateTime.Now.AddSeconds(info.TimeDriftSeconds ?? 0);
            var from = to.AddMinutes(-recordingWindowMinutes);
            foreach (var ch in ChannelRules.ForRecording(info, Device))
            {
                try
                {
                    var obj = Kv(await GetStringAsync("/cgi-bin/mediaFileFind.cgi?action=factory.create", ct))["result"];
                    try
                    {
                        var q = $"/cgi-bin/mediaFileFind.cgi?action=findFile&object={obj}&condition.Channel={ch.Id - 1}&condition.StartTime={Uri.EscapeDataString(from.ToString("yyyy-MM-dd HH:mm:ss"))}&condition.EndTime={Uri.EscapeDataString(to.ToString("yyyy-MM-dd HH:mm:ss"))}&condition.Types[0]=dav";
                        var found = (await GetStringAsync(q, ct)).Trim();
                        ch.Recording = found.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
                        if (ch.Recording == true)
                        {
                            var next = await GetStringAsync($"/cgi-bin/mediaFileFind.cgi?action=findNextFile&object={obj}&count=1", ct);
                            ch.Recording = Kv(next).TryGetValue("found", out var f) && f != "0";
                        }
                    }
                    finally { try { await GetStringAsync($"/cgi-bin/mediaFileFind.cgi?action=destroy&object={obj}", ct); } catch { } }
                    if (ch.Recording == false) info.Problems.Add($"канал {ch.Id}: нет записи за {recordingWindowMinutes} мин");
                }
                catch (Exception ex) { info.Problems.Add($"канал {ch.Id}: поиск записи — {ex.Message}"); }
            }
        }
        return info;
    }

    public Task SetTimeAsync(CancellationToken ct) => SetTimeAsync(DateTimeOffset.Now, ct);

    public Task SetTimeAsync(DateTimeOffset now, CancellationToken ct)
        => GetStringAsync("/cgi-bin/global.cgi?action=setCurrentTime&time=" + Uri.EscapeDataString(now.ToString("yyyy-MM-dd HH:mm:ss")), ct);

    public Task RebootAsync(CancellationToken ct) => GetStringAsync("/cgi-bin/magicBox.cgi?action=reboot", ct);
}

/// <summary>Правила по каналам из настроек устройства: какие проверять, какие игнорировать, у каких запись только по движению.</summary>
public static class ChannelRules
{
    /// <summary>
    /// Помечает каналы режимом (ignored / motion), сортирует, добавляет проблемы «офлайн» по проверяемым.
    /// Игнорируемые остаются в списке (иначе их не вернуть через дашборд), но без статусов и без проблем.
    /// </summary>
    public static void Apply(CctvDeviceInfo info, CctvDevice d)
    {
        info.Channels.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var ch in info.Channels)
        {
            if (d.IgnoreChannels.Contains(ch.Id) || (d.Channels.Count > 0 && !d.Channels.Contains(ch.Id))) { ch.Mode = "ignored"; ch.Online = null; ch.Recording = null; continue; }
            if (d.MotionChannels.Contains(ch.Id)) ch.Mode = "motion";
            if (ch.Online == false) { ch.Recording = false; info.Problems.Add($"канал {ch.Id} офлайн"); }
        }
    }

    /// <summary>Каналы, по которым ищем запись: проверяемые полностью и доступные.</summary>
    public static IEnumerable<CctvChannel> ForRecording(CctvDeviceInfo info, CctvDevice d)
        => info.Channels.Where(c => c.Mode is null && c.Online != false);
}
