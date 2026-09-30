using System;
using System.IO;
using System.Reflection;

namespace Sentinel.Agent.Infrastructure;

/// <summary>Где агент хранит свои файлы: %ProgramData%\Sentinel\Agent.</summary>
public static class AgentPaths
{
    public const string ServiceName = "SentinelAgent";
    public const string ServiceDisplayName = "Sentinel Monitoring Agent";

    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sentinel", "Agent");

    public static string IdentityFile => Path.Combine(Root, "agent.json");
    public static string ConfigCacheFile => Path.Combine(Root, "config.json");
    public static string QueueDb => Path.Combine(Root, "queue.db");
    public static string LogDir => Path.Combine(Root, "logs");

    public static string ExePath => Assembly.GetExecutingAssembly().Location;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);
    }
}
