using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sentinel.Server.Options;

namespace Sentinel.Server.Security;

/// <summary>
/// HTTPS без reverse-proxy (установка на Windows, локальная сеть без домена): сервер сам слушает https.
/// <list type="bullet">
/// <item><c>self-signed</c> — при первом запуске создаётся сертификат в DataDir/tls/server.pfx. Браузер предупредит,
/// а агенты доверяют ему по pin (SHA-256 SubjectPublicKeyInfo), который сервер выдаёт при регистрации.</item>
/// <item><c>pfx</c> — свой сертификат (корпоративный CA, коммерческий, win-acme). Pin не выдаётся: цепочку проверяет Windows.</item>
/// </list>
/// В Docker TLS делают Caddy/nginx — там режим <c>none</c> (по умолчанию).
/// </summary>
public static class ServerTls
{
    public static X509Certificate2? Load(TlsOptions tls, string dataDir, string publicUrl)
    {
        switch (tls.Mode.Trim().ToLowerInvariant())
        {
            case "" or "none":
                return null;
            case "pfx":
                if (string.IsNullOrWhiteSpace(tls.PfxPath)) throw new InvalidOperationException("Sentinel:Tls:Mode = pfx, но не задан Sentinel:Tls:PfxPath.");
                return X509CertificateLoader.LoadPkcs12FromFile(tls.PfxPath, tls.PfxPassword);
            case "self-signed":
                var path = Path.Combine(dataDir, "tls", "server.pfx");
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, CreateSelfSigned(publicUrl));
                }
                // Через файл, а не сразу из CreateSelfSigned: на Windows SChannel не работает с эфемерным ключом.
                return X509CertificateLoader.LoadPkcs12FromFile(path, null);
            default:
                throw new InvalidOperationException($"Sentinel:Tls:Mode = «{tls.Mode}»: допустимо none, self-signed или pfx.");
        }
    }

    /// <summary>Pin выдаётся агентам только для самоподписанного: свой pfx проверяется обычной цепочкой и может перевыпускаться.</summary>
    public static string? PinFor(TlsOptions tls, X509Certificate2? cert)
        => cert is not null && tls.Mode.Trim().Equals("self-signed", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()))
            : null;

    private static byte[] CreateSelfSigned(string publicUrl)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Sentinel Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", Environment.MachineName };
        try { names.Add(Dns.GetHostEntry(Environment.MachineName).HostName); } catch { /* без DNS — только короткое имя */ }
        if (Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0) names.Add(uri.Host);
        foreach (var n in names)
        {
            if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(n);
        }
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        // Надолго: агенты привязаны к ключу (pin), замена сертификата = перерегистрация агентов.
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return cert.Export(X509ContentType.Pfx);
    }

    /// <summary>PEM сертификата — для /agent/ca.crt (install.ps1 и агент сохраняют его как доверенный корень).</summary>
    public static string ToPem(X509Certificate2 cert) => cert.ExportCertificatePem();
}
