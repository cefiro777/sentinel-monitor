using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Sentinel.Server.Options;

namespace Sentinel.Server.Services;

/// <summary>
/// Pin сертификата для агентов: из настроек, из собственного самоподписанного сертификата сервера (Sentinel:Tls)
/// или из корневого сертификата внутреннего CA Caddy (режим без домена).
/// Файл читается лениво — при первом запуске Caddy создаёт CA уже после старта сервера.
/// </summary>
public sealed class CertificatePinProvider(IOptions<SentinelOptions> options, ServerCertificate own)
{
    private string? _cached;
    private DateTime _cachedAt;

    public string? Get()
    {
        var configured = options.Value.CertificatePin;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (own.Pin is not null) return own.Pin;

        if (DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(1)) return _cached;
        _cachedAt = DateTime.UtcNow;
        try
        {
            var path = options.Value.CaddyRootCertPath;
            if (!File.Exists(path)) return _cached = null;
            using var root = X509CertificateLoader.LoadCertificateFromFile(path);
            return _cached = Convert.ToBase64String(SHA256.HashData(root.PublicKey.ExportSubjectPublicKeyInfo()));
        }
        catch { return _cached = null; }
    }
}

/// <summary>Сертификат, который сервер сам отдаёт по https (Sentinel:Tls), и его pin для агентов. Null — TLS снаружи.</summary>
public sealed record ServerCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate, string? Pin);
