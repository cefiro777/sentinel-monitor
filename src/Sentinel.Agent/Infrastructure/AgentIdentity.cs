using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Infrastructure;

/// <summary>
/// Локальная идентичность агента: адрес сервера, AgentId, секрет (под DPAPI LocalMachine),
/// публичный ключ сервера и белый список команд. Файл agent.json.
/// </summary>
public sealed class AgentIdentity
{
    public string ServerUrl { get; set; } = "";
    public Guid AgentId { get; set; }
    public Guid HostId { get; set; }
    /// <summary>Секрет HMAC, зашифрованный DPAPI (base64). В открытом виде на диск не попадает.</summary>
    public string SecretProtected { get; set; } = "";
    public string ServerPublicKey { get; set; } = "";
    public string? CertificatePin { get; set; }
    /// <summary>
    /// Прокси для связи с сервером мониторинга: пусто или «system» — системные настройки учётки службы (как у браузера),
    /// «none» — напрямую, иначе адрес вида http://proxy:3128. Нужен, когда у LocalSystem прописан чужой/мёртвый прокси (частая история на DC).
    /// </summary>
    public string? Proxy { get; set; }
    /// <summary>Корневой сертификат сервера (DER, base64) для режима без домена; его SPKI совпадает с CertificatePin.</summary>
    public string? CaCertificate { get; set; }
    public DateTimeOffset EnrolledAt { get; set; }

    public System.Security.Cryptography.X509Certificates.X509Certificate2? GetCaCertificate()
    {
        if (string.IsNullOrEmpty(CaCertificate)) return null;
        try { return new System.Security.Cryptography.X509Certificates.X509Certificate2(Convert.FromBase64String(CaCertificate)); }
        catch { return null; }
    }

    /// <summary>
    /// Разрешённые типы команд. Задаётся при установке, сервером не меняется.
    /// Поддерживаются маски вида "service.*".
    /// </summary>
    public List<string> AllowedCommands { get; set; } = new List<string>
    {
        CommandTypes.Ping, CommandTypes.ConfigRefresh, CommandTypes.AgentUpdate, "service.*", "backup.*", "cctv.*",
    };

    public bool IsEnrolled => AgentId != Guid.Empty && !string.IsNullOrEmpty(SecretProtected);

    public byte[] GetSecret()
    {
        var protectedBytes = Convert.FromBase64String(SecretProtected);
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
    }

    public void SetSecret(byte[] secret)
    {
        SecretProtected = Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.LocalMachine));
    }

    public bool IsCommandAllowed(string type)
    {
        foreach (var pattern in AllowedCommands)
        {
            if (string.Equals(pattern, type, StringComparison.OrdinalIgnoreCase)) return true;
            if (pattern.EndsWith("*") && type.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Sentinel.Agent.v1");

    public static AgentIdentity? Load()
    {
        if (!File.Exists(AgentPaths.IdentityFile)) return null;
        return SentinelJson.Deserialize<AgentIdentity>(File.ReadAllText(AgentPaths.IdentityFile, Encoding.UTF8));
    }

    public void Save()
    {
        AgentPaths.EnsureDirectories();
        var tmp = AgentPaths.IdentityFile + ".tmp";
        File.WriteAllText(tmp, SentinelJson.Serialize(this), Encoding.UTF8);
        if (File.Exists(AgentPaths.IdentityFile)) File.Replace(tmp, AgentPaths.IdentityFile, null);
        else File.Move(tmp, AgentPaths.IdentityFile);
    }
}
