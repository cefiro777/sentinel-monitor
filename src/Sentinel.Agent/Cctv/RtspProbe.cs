using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Sentinel.Agent.Cctv;

/// <summary>
/// Минимальный RTSP-клиент: OPTIONS → DESCRIBE → SETUP (RTP/AVP/TCP interleaved) → PLAY → ждём первый RTP-пакет.
/// Так проверяется не «порт открыт», а что камера реально отдаёт видео. Basic/Digest-авторизация.
/// </summary>
public static class RtspProbe
{
    public sealed class Result
    {
        public bool DescribeOk;
        public bool DataReceived;
        public string? Error;
        public string? Codec;
        public int Tracks;
        public double LatencyMs;
    }

    public static async Task<Result> ProbeAsync(string url, bool requireData, TimeSpan timeout, CancellationToken ct)
    {
        var result = new Result();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Uri uri;
        try { uri = new Uri(url); } catch { result.Error = "некорректный URL"; return result; }
        string? user = null, pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(new[] { ':' }, 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        var cleanUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString().TrimEnd('/');
        if (uri.AbsolutePath.Length > 1 || url.EndsWith("/")) cleanUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString();
        var port = uri.IsDefaultPort ? 554 : uri.Port;

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                using (var client = new TcpClient())
                // На .NET Framework NetworkStream.ReadAsync не реагирует на CancellationToken — по таймауту закрываем сокет,
                // и висящее чтение завершается исключением.
                using (cts.Token.Register(() => { try { client.Close(); } catch { } }))
                {
                    var connect = client.ConnectAsync(uri.Host, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeout, cts.Token)) != connect) { result.Error = "таймаут подключения"; return result; }
                    await connect;
                    client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                    var stream = client.GetStream();
                    var session = new Session(stream, cleanUrl, user, pass);

                    var describe = await session.RequestAsync("DESCRIBE", cleanUrl, "Accept: application/sdp", cts.Token);
                    if (describe.Code == 401 && user is not null) { session.ApplyAuth(describe); describe = await session.RequestAsync("DESCRIBE", cleanUrl, "Accept: application/sdp", cts.Token); }
                    if (describe.Code == 401) { result.Error = "неверный логин/пароль (401)"; return result; }
                    if (describe.Code == 404) { result.Error = "поток не найден (404) — проверьте путь/канал"; return result; }
                    if (describe.Code != 200) { result.Error = $"DESCRIBE → {describe.Code} {describe.Reason}"; return result; }
                    result.DescribeOk = true;
                    result.LatencyMs = sw.Elapsed.TotalMilliseconds;

                    var sdp = describe.Body;
                    var media = ParseSdp(sdp, describe.Headers.TryGetValue("content-base", out var cb) ? cb : cleanUrl);
                    result.Tracks = media.Count;
                    result.Codec = media.Count > 0 ? media[0].codec : null;
                    if (!requireData || media.Count == 0) return result;

                    var track = media[0];
                    var setup = await session.RequestAsync("SETUP", track.control, "Transport: RTP/AVP/TCP;unicast;interleaved=0-1", cts.Token);
                    if (setup.Code != 200) { result.Error = $"SETUP → {setup.Code} {setup.Reason}"; return result; }
                    if (setup.Headers.TryGetValue("session", out var sess)) session.SessionId = sess.Split(';')[0].Trim();

                    var play = await session.RequestAsync("PLAY", cleanUrl, "Range: npt=0.000-", cts.Token);
                    if (play.Code != 200) { result.Error = $"PLAY → {play.Code} {play.Reason}"; return result; }

                    // Ждём interleaved-пакет: байт '$', канал, длина. Это и есть первые байты видео.
                    result.DataReceived = await session.WaitForInterleavedAsync(cts.Token);
                    if (!result.DataReceived) result.Error = "PLAY принят, но данные не пришли";
                    try { await session.RequestAsync("TEARDOWN", cleanUrl, null, cts.Token); } catch { }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result.Error ??= "таймаут"; }
            catch (Exception ex) { result.Error ??= cts.IsCancellationRequested && !ct.IsCancellationRequested ? "таймаут ожидания данных" : ex.Message; }
        }
        return result;
    }

    private static List<(string control, string codec)> ParseSdp(string sdp, string baseUrl)
    {
        var list = new List<(string, string)>();
        string? control = null, codec = null;
        var inMedia = false;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("m="))
            {
                if (inMedia) list.Add((Resolve(control, baseUrl), codec ?? "?"));
                inMedia = true; control = null; codec = null;
                if (!line.StartsWith("m=video")) inMedia = false; // интересует видео
            }
            else if (inMedia && line.StartsWith("a=control:")) control = line.Substring(10).Trim();
            else if (inMedia && line.StartsWith("a=rtpmap:")) { var m = Regex.Match(line, @"a=rtpmap:\d+\s+([^/]+)"); if (m.Success) codec = m.Groups[1].Value; }
        }
        if (inMedia) list.Add((Resolve(control, baseUrl), codec ?? "?"));
        return list;
    }

    private static string Resolve(string? control, string baseUrl)
    {
        if (string.IsNullOrEmpty(control) || control == "*") return baseUrl;
        if (control!.StartsWith("rtsp://")) return control;
        return baseUrl.TrimEnd('/') + "/" + control.TrimStart('/');
    }

    private sealed class Session
    {
        private readonly NetworkStream _stream;
        private readonly string _url;
        private readonly string? _user, _pass;
        private int _cseq;
        private string? _authHeader;
        private string? _realm, _nonce;
        private bool _basic;
        public string? SessionId;

        public Session(NetworkStream stream, string url, string? user, string? pass) { _stream = stream; _url = url; _user = user; _pass = pass; }

        public sealed class Response { public int Code; public string Reason = ""; public Dictionary<string, string> Headers = new Dictionary<string, string>(); public string Body = ""; }

        public void ApplyAuth(Response r)
        {
            if (!r.Headers.TryGetValue("www-authenticate", out var wa)) return;
            if (wa.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            {
                _realm = Regex.Match(wa, "realm=\"([^\"]*)\"").Groups[1].Value;
                _nonce = Regex.Match(wa, "nonce=\"([^\"]*)\"").Groups[1].Value;
                _basic = false;
            }
            else _basic = true;
        }

        private string? BuildAuth(string method, string uri)
        {
            if (_user is null) return null;
            if (_basic) return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_user + ":" + _pass));
            if (_nonce is null) return null;
            var ha1 = Md5($"{_user}:{_realm}:{_pass}");
            var ha2 = Md5($"{method}:{uri}");
            var resp = Md5($"{ha1}:{_nonce}:{ha2}");
            return $"Authorization: Digest username=\"{_user}\", realm=\"{_realm}\", nonce=\"{_nonce}\", uri=\"{uri}\", response=\"{resp}\"";
        }

        public async Task<Response> RequestAsync(string method, string uri, string? extraHeader, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append($"{method} {uri} RTSP/1.0\r\nCSeq: {++_cseq}\r\nUser-Agent: SentinelAgent\r\n");
            if (SessionId is not null) sb.Append($"Session: {SessionId}\r\n");
            var auth = BuildAuth(method, uri);
            if (auth is not null) sb.Append(auth).Append("\r\n");
            if (extraHeader is not null) sb.Append(extraHeader).Append("\r\n");
            sb.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await _stream.WriteAsync(bytes, 0, bytes.Length, ct);
            return await ReadResponseAsync(ct);
        }

        private async Task<Response> ReadResponseAsync(CancellationToken ct)
        {
            var header = new StringBuilder();
            var buf = new byte[1];
            // Заголовок до пустой строки; пропускаем возможные interleaved-пакеты, если они уже пошли.
            while (!header.ToString().EndsWith("\r\n\r\n"))
            {
                var n = await _stream.ReadAsync(buf, 0, 1, ct);
                if (n == 0) throw new IOException("соединение закрыто");
                if (header.Length == 0 && buf[0] == '$') { await SkipInterleavedAsync(ct); continue; }
                header.Append((char)buf[0]);
                if (header.Length > 64 * 1024) throw new IOException("слишком длинный ответ");
            }
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var r = new Response();
            var status = Regex.Match(lines[0], @"RTSP/\d\.\d\s+(\d+)\s*(.*)");
            r.Code = status.Success ? int.Parse(status.Groups[1].Value) : 0;
            r.Reason = status.Success ? status.Groups[2].Value : lines[0];
            for (var i = 1; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf(':');
                if (idx > 0) r.Headers[lines[i].Substring(0, idx).Trim().ToLowerInvariant()] = lines[i].Substring(idx + 1).Trim();
            }
            if (r.Headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var len) && len > 0)
            {
                var body = new byte[len];
                var read = 0;
                while (read < len) { var n = await _stream.ReadAsync(body, read, len - read, ct); if (n == 0) break; read += n; }
                r.Body = Encoding.UTF8.GetString(body, 0, read);
            }
            return r;
        }

        private async Task SkipInterleavedAsync(CancellationToken ct)
        {
            var hdr = new byte[3];
            var read = 0;
            while (read < 3) { var n = await _stream.ReadAsync(hdr, read, 3 - read, ct); if (n == 0) throw new IOException("закрыто"); read += n; }
            var len = (hdr[1] << 8) | hdr[2];
            var skip = new byte[len];
            read = 0;
            while (read < len) { var n = await _stream.ReadAsync(skip, read, len - read, ct); if (n == 0) throw new IOException("закрыто"); read += n; }
        }

        public async Task<bool> WaitForInterleavedAsync(CancellationToken ct)
        {
            var buf = new byte[4];
            for (var i = 0; i < 4096; i++)
            {
                var n = await _stream.ReadAsync(buf, 0, 1, ct);
                if (n == 0) return false;
                if (buf[0] == '$')
                {
                    var read = 0;
                    while (read < 3) { n = await _stream.ReadAsync(buf, 1 + read, 3 - read, ct); if (n == 0) return false; read += n; }
                    var len = (buf[2] << 8) | buf[3];
                    return len > 12; // RTP-пакет с полезной нагрузкой
                }
            }
            return false;
        }

        private static string Md5(string s)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
