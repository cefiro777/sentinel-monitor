using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;
using Sentinel.Contracts.Security;

namespace Sentinel.Agent.Infrastructure;

/// <summary>REST-клиент сервера с HMAC-подписью запросов и pinning сертификата.</summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AgentIdentity? _identity;
    private readonly ILogger _log;

    /// <summary>Разница часов сервера и агента (server - local), уточняется при каждом ответе сервера.</summary>
    public TimeSpan ClockOffset { get; private set; }
    public DateTimeOffset? ClockOffsetMeasuredAt { get; private set; }

    public ServerClient(string serverUrl, AgentIdentity? identity, ILogger log)
    {
        _identity = identity;
        _log = log;
        ConfigureTls();

        var handler = CreateHandler(identity, _log);
        handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SentinelAgent/" + AgentPaths.Version);
    }

    /// <summary>
    /// Обработчик HTTP для всех соединений с сервером мониторинга (запросы, SignalR, обновление): прокси и pin из настроек агента.
    /// </summary>
    public static HttpClientHandler CreateHandler(AgentIdentity? identity, ILogger log)
    {
        var handler = new HttpClientHandler();
        var proxy = ResolveProxy(identity);
        if (proxy is null) handler.UseProxy = false;
        else if (proxy != SystemProxy) { handler.UseProxy = true; handler.Proxy = proxy; }
        if (identity is not null && !string.IsNullOrWhiteSpace(identity.CertificatePin))
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) => TlsPinning.Validate(cert, chain, errors, identity, log);
        return handler;
    }

    /// <summary>Маркер «системный прокси» — чтобы отличать его от «без прокси» (null).</summary>
    public static readonly IWebProxy SystemProxy = new WebProxy();

    /// <summary>null — без прокси, SystemProxy — системные настройки, иначе явный прокси.</summary>
    public static IWebProxy? ResolveProxy(AgentIdentity? identity)
    {
        var value = identity?.Proxy?.Trim();
        if (string.IsNullOrEmpty(value) || value!.Equals("system", StringComparison.OrdinalIgnoreCase)) return SystemProxy;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("direct", StringComparison.OrdinalIgnoreCase)) return null;
        return new WebProxy(value.Contains("://") ? value : "http://" + value) { BypassProxyOnLocal = false };
    }

    /// <summary>Человекочитаемое описание: какой прокси реально будет использован для адреса сервера.</summary>
    public static string DescribeProxy(AgentIdentity? identity, Uri server)
    {
        var proxy = ResolveProxy(identity);
        if (proxy is null) return "напрямую (настройка агента: none)";
        if (proxy != SystemProxy) return $"{proxy.GetProxy(server)} (настройка агента)";
        var system = WebRequest.DefaultWebProxy?.GetProxy(server);
        return system is null || system == server ? "напрямую (системные настройки)" : $"{system} (системные настройки этой учётки)";
    }

    public static void ConfigureTls()
    {
        // Server 2008 R2 по умолчанию не включает TLS 1.2 — включаем явно. TLS 1.3 — если ОС умеет.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } // Tls13
        catch (NotSupportedException) { }
        ServicePointManager.Expect100Continue = false;
    }

    public static string SpkiSha256(X509Certificate2 cert) => TlsPinning.SpkiSha256(cert);

    /// <summary>
    /// Режим без домена: скачивает корневой сертификат сервера (/agent/ca.crt), проверяет его SPKI по pin и сохраняет в identity.
    /// Само скачивание идёт без проверки TLS — доверие даёт именно сравнение с pin, переданным инженером при установке.
    /// </summary>
    public static async Task FetchCaCertificateAsync(AgentIdentity identity, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identity.CertificatePin)) return;
        ConfigureTls();
        var handler = CreateHandler(identity, log);
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
        {
            var resp = await http.GetAsync(identity.ServerUrl.TrimEnd('/') + "/agent/ca.crt", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) { log.LogInformation("Сервер не раздаёт корневой сертификат — pin проверяется по системной цепочке"); return; }
            resp.EnsureSuccessStatusCode();
            var pem = await resp.Content.ReadAsStringAsync();
            var b64 = pem.Replace("-----BEGIN CERTIFICATE-----", "").Replace("-----END CERTIFICATE-----", "").Replace("\r", "").Replace("\n", "").Trim();
            var ca = new X509Certificate2(Convert.FromBase64String(b64));
            if (TlsPinning.SpkiSha256(ca) != identity.CertificatePin) throw new InvalidOperationException("Корневой сертификат сервера не совпадает с pin — проверьте адрес сервера и pin.");
            identity.CaCertificate = b64;
            log.LogInformation("Корневой сертификат сервера сохранён ({Subject}, до {NotAfter:d})", ca.Subject, ca.NotAfter);
        }
    }

    // ---- API ----

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        var resp = await SendAsync(HttpMethod.Post, "api/agent/enroll", request, sign: false, ct);
        return await ReadAsync<EnrollResponse>(resp);
    }

    public async Task<AgentConfigDocument> GetConfigAsync(CancellationToken ct)
    {
        var resp = await SendAsync(HttpMethod.Get, "api/agent/config", null, sign: true, ct);
        return await ReadAsync<AgentConfigDocument>(resp);
    }

    public async Task<ResultsAck> PostResultsAsync(ResultsBatch batch, CancellationToken ct)
    {
        var resp = await SendAsync(HttpMethod.Post, "api/agent/results", batch, sign: true, ct);
        var ack = await ReadAsync<ResultsAck>(resp);
        UpdateClock(ack.ServerTime);
        return ack;
    }

    /// <summary>Короткоживущий токен для подключения к SignalR-хабу.</summary>
    public string CreateHubAccessToken()
    {
        if (_identity is null) throw new InvalidOperationException("Агент не зарегистрирован.");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = AgentAuth.NewNonce().Replace(".", "_");
        var canonical = AgentAuth.CanonicalString("CONNECT", AgentHubContract.Path, ts, nonce, AgentAuth.Sha256Hex(Array.Empty<byte>()));
        var sig = AgentAuth.Sign(_identity.GetSecret(), canonical);
        return $"{_identity.AgentId:D}.{ts}.{nonce}.{sig}";
    }

    // ---- внутреннее ----

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool sign, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path);
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(SentinelJson.Serialize(body));
        if (body is not null)
        {
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        if (sign)
        {
            if (_identity is null) throw new InvalidOperationException("Агент не зарегистрирован.");
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = AgentAuth.NewNonce();
            var canonical = AgentAuth.CanonicalString(method.Method, "/" + path, ts, nonce, AgentAuth.Sha256Hex(bodyBytes));
            req.Headers.Add(AgentAuth.HeaderAgentId, _identity.AgentId.ToString("D"));
            req.Headers.Add(AgentAuth.HeaderTimestamp, ts.ToString());
            req.Headers.Add(AgentAuth.HeaderNonce, nonce);
            req.Headers.Add(AgentAuth.HeaderSignature, AgentAuth.Sign(_identity.GetSecret(), canonical));
        }

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (resp.Headers.Date is DateTimeOffset serverDate) UpdateClock(serverDate);

        if (!resp.IsSuccessStatusCode)
        {
            var text = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync();
            throw new ServerApiException(resp.StatusCode, $"{method} /{path} → {(int)resp.StatusCode}: {Truncate(text, 300)}");
        }
        return resp;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        return SentinelJson.Deserialize<T>(json) ?? throw new InvalidOperationException("Пустой ответ сервера.");
    }

    private void UpdateClock(DateTimeOffset serverTime)
    {
        ClockOffset = serverTime - DateTimeOffset.UtcNow;
        ClockOffsetMeasuredAt = DateTimeOffset.UtcNow;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => _http.Dispose();
}

public sealed class ServerApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public ServerApiException(HttpStatusCode status, string message) : base(message) => StatusCode = status;
}
