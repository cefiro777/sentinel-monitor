using System;
using System.IO;
using System.Text;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Infrastructure;

/// <summary>Текущая конфигурация агента + кэш на диске для работы без связи с сервером.</summary>
public sealed class ConfigStore
{
    private readonly object _lock = new object();
    private AgentConfigDocument _current = new AgentConfigDocument { Version = 0 };

    public event Action<AgentConfigDocument>? Changed;

    public AgentConfigDocument Current
    {
        get { lock (_lock) return _current; }
    }

    public int Version => Current.Version;

    public bool LoadCache()
    {
        if (!File.Exists(AgentPaths.ConfigCacheFile)) return false;
        try
        {
            var doc = SentinelJson.Deserialize<AgentConfigDocument>(File.ReadAllText(AgentPaths.ConfigCacheFile, Encoding.UTF8));
            if (doc is null) return false;
            lock (_lock) _current = doc;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Применяет новый конфиг; возвращает true, если версия изменилась.</summary>
    public bool Apply(AgentConfigDocument doc)
    {
        lock (_lock)
        {
            if (doc.Version == _current.Version && _current.Checks.Count > 0) return false;
            _current = doc;
        }
        try
        {
            AgentPaths.EnsureDirectories();
            File.WriteAllText(AgentPaths.ConfigCacheFile, SentinelJson.Serialize(doc), Encoding.UTF8);
        }
        catch { /* кэш — не критично */ }

        Changed?.Invoke(doc);
        return true;
    }
}
