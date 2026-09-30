using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Sentinel.Agent.Infrastructure;

/// <summary>
/// Проверка сертификата сервера по pin (SHA-256 от SubjectPublicKeyInfo).
/// Если у агента есть сохранённый корневой сертификат (режим «по IP» с внутренним CA), цепочка строится с ним —
/// Windows такой корень не знает и сам в цепочку не включит.
/// </summary>
public static class TlsPinning
{
    public static bool Validate(X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors, AgentIdentity identity, ILogger log)
    {
        var pin = identity.CertificatePin;
        if (string.IsNullOrWhiteSpace(pin)) return errors == SslPolicyErrors.None;
        if (cert is null) return false;

        if (SpkiSha256(cert) == pin) return true;
        if (chain is not null && ChainHasPin(chain, pin!)) return true;

        // Строим цепочку с нашим корнем: если лист действительно выпущен пиненным CA, корень окажется в конце цепочки.
        var ca = identity.GetCaCertificate();
        if (ca is not null)
        {
            using (var custom = new X509Chain())
            {
                custom.ChainPolicy.ExtraStore.Add(ca);
                // Промежуточные сертификаты из рукопожатия — иначе цепочку до нашего корня не собрать.
                if (chain is not null) foreach (var el in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(el.Certificate);
                custom.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                custom.Build(cert);
                for (var i = 0; i < custom.ChainElements.Count; i++)
                    if (SpkiSha256(custom.ChainElements[i].Certificate) == pin) return true;
                log.LogDebug("TLS: цепочка с нашим корнем: {Chain}", string.Join(" -> ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<X509ChainElement>(custom.ChainElements), e => e.Certificate.Subject + " [" + e.ChainElementStatus.Length + "]")));
            }
        }

        log.LogError(errors == SslPolicyErrors.None
            ? "TLS: pin сертификата не совпал. Возможен перехват трафика или сменился ключ сервера."
            : "TLS: сертификат сервера не прошёл проверку ({Errors}) и pin не совпал.", errors);
        return false;
    }

    private static bool ChainHasPin(X509Chain chain, string pin)
    {
        foreach (var el in chain.ChainElements)
            if (SpkiSha256(el.Certificate) == pin) return true;
        return false;
    }

    public static string SpkiSha256(X509Certificate2 cert)
    {
        var bc = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(cert.RawData);
        var spki = bc.SubjectPublicKeyInfo.GetDerEncoded();
        using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(spki));
    }
}
