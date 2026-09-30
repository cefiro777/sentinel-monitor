using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Sentinel.Agent.Cctv;

/// <summary>ONVIF Device/Media через сырой SOAP: без генерации WCF-прокси, работает с любой прошивкой.</summary>
public sealed class OnvifClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user, _pass;
    private string? _mediaXAddr;

    /// <summary>Разница часов устройства и агента (device - local), измеряется в GetSystemDateAndTime.</summary>
    public TimeSpan? DeviceClockOffset { get; private set; }

    public OnvifClient(string host, int port, string user, string pass, TimeSpan timeout)
    {
        _host = host; _port = port; _user = user; _pass = pass;
        _http = new HttpClient { Timeout = timeout };
    }

    private string DeviceUrl => $"http://{_host}:{_port}/onvif/device_service";

    public sealed class DeviceInfo { public string? Manufacturer, Model, Firmware, Serial; }
    public sealed class Profile { public string Token = ""; public string Name = ""; public string? StreamUri; }

    /// <summary>Единственный вызов, который по стандарту не требует аутентификации — и даёт время устройства.</summary>
    public async Task<DateTimeOffset> GetSystemDateAndTimeAsync(CancellationToken ct)
    {
        var body = "<tds:GetSystemDateAndTime xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"/>";
        var doc = await CallAsync(DeviceUrl, body, auth: false, ct);
        var utc = doc.Descendants().FirstOrDefaultLocal("UTCDateTime");
        var el = utc ?? doc.Descendants().FirstOrDefaultLocal("LocalDateTime") ?? throw new InvalidOperationException("ONVIF: нет времени в ответе");
        int Get(string parent, string name) => int.Parse(el.Descendants().FirstOrDefaultLocal(parent)!.Descendants().FirstOrDefaultLocal(name)!.Value);
        var dt = new DateTime(Get("Date", "Year"), Get("Date", "Month"), Get("Date", "Day"), Get("Time", "Hour"), Get("Time", "Minute"), Get("Time", "Second"));
        var result = utc is not null ? new DateTimeOffset(dt, TimeSpan.Zero) : new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        DeviceClockOffset = result - DateTimeOffset.UtcNow;
        return result;
    }

    public async Task<DeviceInfo> GetDeviceInformationAsync(CancellationToken ct)
    {
        var doc = await CallAsync(DeviceUrl, "<tds:GetDeviceInformation xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"/>", true, ct);
        return new DeviceInfo
        {
            Manufacturer = doc.Descendants().FirstOrDefaultLocal("Manufacturer")?.Value,
            Model = doc.Descendants().FirstOrDefaultLocal("Model")?.Value,
            Firmware = doc.Descendants().FirstOrDefaultLocal("FirmwareVersion")?.Value,
            Serial = doc.Descendants().FirstOrDefaultLocal("SerialNumber")?.Value,
        };
    }

    public async Task<List<Profile>> GetProfilesAsync(bool withStreamUris, CancellationToken ct)
    {
        var media = await GetMediaXAddrAsync(ct);
        var doc = await CallAsync(media, "<trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"/>", true, ct);
        var profiles = new List<Profile>();
        foreach (var p in doc.Descendants().WhereLocal("Profiles"))
        {
            var token = p.Attribute("token")?.Value ?? "";
            var name = p.Elements().FirstOrDefaultLocal("Name")?.Value ?? token;
            profiles.Add(new Profile { Token = token, Name = name });
        }
        if (withStreamUris)
        {
            foreach (var p in profiles)
            {
                try
                {
                    var body = "<trt:GetStreamUri xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\" xmlns:tt=\"http://www.onvif.org/ver10/schema\">" +
                               "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>" +
                               $"<trt:ProfileToken>{System.Security.SecurityElement.Escape(p.Token)}</trt:ProfileToken></trt:GetStreamUri>";
                    var uri = await CallAsync(media, body, true, ct);
                    p.StreamUri = uri.Descendants().FirstOrDefaultLocal("Uri")?.Value;
                }
                catch { /* профиль без потока — не ошибка устройства */ }
            }
        }
        return profiles;
    }

    private async Task<string> GetMediaXAddrAsync(CancellationToken ct)
    {
        if (_mediaXAddr is not null) return _mediaXAddr;
        try
        {
            var doc = await CallAsync(DeviceUrl, "<tds:GetCapabilities xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><tds:Category>Media</tds:Category></tds:GetCapabilities>", true, ct);
            var xaddr = doc.Descendants().FirstOrDefaultLocal("Media")?.Descendants().FirstOrDefaultLocal("XAddr")?.Value;
            if (!string.IsNullOrEmpty(xaddr))
            {
                // Некоторые устройства возвращают XAddr с внутренним адресом — подменяем хост на тот, по которому обращаемся.
                var b = new UriBuilder(xaddr) { Host = _host };
                if (b.Port == 80 && _port != 80) b.Port = _port;
                return _mediaXAddr = b.Uri.ToString();
            }
        }
        catch { }
        return _mediaXAddr = $"http://{_host}:{_port}/onvif/media_service";
    }

    private async Task<XDocument> CallAsync(string url, string bodyXml, bool auth, CancellationToken ct)
    {
        var header = auth ? "<s:Header>" + UsernameToken() + "</s:Header>" : "";
        var envelope = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" + header + "<s:Body>" + bodyXml + "</s:Body></s:Envelope>";
        using (var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml"))
        using (var resp = await _http.PostAsync(url, content, ct))
        {
            var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
            XDocument doc;
            try { doc = XDocument.Parse(text); }
            catch { throw new InvalidOperationException($"ONVIF: HTTP {(int)resp.StatusCode}, ответ не XML"); }
            var fault = doc.Descendants().FirstOrDefaultLocal("Fault");
            if (fault is not null)
            {
                var reason = fault.Descendants().FirstOrDefaultLocal("Text")?.Value ?? fault.Descendants().FirstOrDefaultLocal("Value")?.Value ?? "SOAP Fault";
                if (reason.Contains("NotAuthorized") || reason.Contains("Sender") && (int)resp.StatusCode == 401) reason = "неверный логин/пароль";
                throw new InvalidOperationException("ONVIF: " + reason.Trim());
            }
            if ((int)resp.StatusCode == 401) throw new InvalidOperationException("ONVIF: неверный логин/пароль (401)");
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"ONVIF: HTTP {(int)resp.StatusCode}");
            return doc;
        }
    }

    /// <summary>WS-Security UsernameToken с PasswordDigest = Base64(SHA1(nonce + created + password)). Время — с поправкой на часы устройства.</summary>
    private string UsernameToken()
    {
        var nonce = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
        var created = (DateTimeOffset.UtcNow + (DeviceClockOffset ?? TimeSpan.Zero)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passBytes = Encoding.UTF8.GetBytes(_pass);
        var all = new byte[nonce.Length + createdBytes.Length + passBytes.Length];
        Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, all, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passBytes, 0, all, nonce.Length + createdBytes.Length, passBytes.Length);
        string digest;
        using (var sha = SHA1.Create()) digest = Convert.ToBase64String(sha.ComputeHash(all));
        return "<wsse:Security s:mustUnderstand=\"1\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\" xmlns:wsu=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
               $"<wsse:UsernameToken><wsse:Username>{System.Security.SecurityElement.Escape(_user)}</wsse:Username>" +
               $"<wsse:Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">{digest}</wsse:Password>" +
               $"<wsse:Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{Convert.ToBase64String(nonce)}</wsse:Nonce>" +
               $"<wsu:Created>{created}</wsu:Created></wsse:UsernameToken></wsse:Security>";
    }

    public void Dispose() => _http.Dispose();
}

internal static class XmlLocalExt
{
    public static XElement? FirstOrDefaultLocal(this IEnumerable<XElement> els, string localName)
    {
        foreach (var e in els) if (e.Name.LocalName == localName) return e;
        return null;
    }

    public static IEnumerable<XElement> WhereLocal(this IEnumerable<XElement> els, string localName)
    {
        foreach (var e in els) if (e.Name.LocalName == localName) yield return e;
    }
}
