using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Infrastructure;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Backup;

/// <summary>Модуль, который запускается по расписанию, а не по интервалу.</summary>
public interface IScheduledModule : IModule
{
    /// <summary>Когда запускать следующий раз. null — расписание не задано, модуль не запускается сам.</summary>
    DateTimeOffset? NextRun(CheckConfig check, DateTimeOffset? lastRun, DateTimeOffset now);
}

/// <summary>Время последнего запуска каждого задания и последнего полного бэкапа — переживает перезапуск службы.</summary>
public sealed class RunStateStore
{
    private static readonly string File = Path.Combine(AgentPaths.Root, "schedule.json");
    private static readonly object Lock = new object();

    public sealed class Entry
    {
        public DateTimeOffset? LastRun { get; set; }
        public DateTimeOffset? LastFull { get; set; }
        public string? LastFullArtifact { get; set; }
    }

    public static Entry Get(Guid checkId)
    {
        lock (Lock)
        {
            var all = Load();
            return all.TryGetValue(checkId.ToString(), out var e) ? e : new Entry();
        }
    }

    public static void Update(Guid checkId, Action<Entry> mutate)
    {
        lock (Lock)
        {
            var all = Load();
            if (!all.TryGetValue(checkId.ToString(), out var e)) all[checkId.ToString()] = e = new Entry();
            mutate(e);
            AgentPaths.EnsureDirectories();
            System.IO.File.WriteAllText(File, SentinelJson.Serialize(all), Encoding.UTF8);
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return SentinelJson.Deserialize<Dictionary<string, Entry>>(System.IO.File.ReadAllText(File, Encoding.UTF8)) ?? new Dictionary<string, Entry>();
        }
        catch { }
        return new Dictionary<string, Entry>();
    }
}

/// <summary>Журнал одного запуска: пишется в лог агента и уходит на сервер вместе с отчётом.</summary>
public sealed class BackupLog
{
    private readonly ILogger _log;
    private readonly string _job;
    public List<string> Lines { get; } = new List<string>();

    public BackupLog(ILogger log, string job) { _log = log; _job = job; }

    public void Info(string message)
    {
        Lines.Add($"{DateTime.Now:HH:mm:ss} {message}");
        _log.LogInformation("[{Job}] {Message}", _job, message);
    }

    public void Warn(string message)
    {
        Lines.Add($"{DateTime.Now:HH:mm:ss} ! {message}");
        _log.LogWarning("[{Job}] {Message}", _job, message);
    }
}

public static class BackupUtil
{
    /// <summary>
    /// Проверяет путь до того, как в него полезут: иначе .NET отвечает «Указан недопустимый путь» без указания, какой именно.
    /// Заодно ловим частую ошибку — лишние обратные слеши в UNC-пути.
    /// </summary>
    public static string ValidatePath(string path, string what)
    {
        var value = (path ?? "").Trim();
        if (value.Length == 0) throw new InvalidOperationException($"Не задан путь: {what}.");
        if (value.StartsWith(@"\\\\")) throw new InvalidOperationException(
            $"{what}: «{value}» — сетевой путь должен начинаться ровно с двух обратных слешей, например " + @"\\server\share" + ".");
        var bad = value.IndexOfAny(System.IO.Path.GetInvalidPathChars());
        if (bad >= 0) throw new InvalidOperationException($"{what}: «{value}» содержит недопустимый символ «{value[bad]}».");
        try { return System.IO.Path.GetFullPath(value); }
        catch (Exception ex) { throw new InvalidOperationException($"{what}: «{value}» — {ex.Message}"); }
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? "job" : s;
    }

    public static string Sha256File(string path)
    {
        using (var sha = SHA256.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(64);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public static string Md5File(string path)
    {
        using (var md5 = MD5.Create())
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var hash = md5.ComputeHash(fs);
            var sb = new StringBuilder(32);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public static string HumanSize(long bytes)
    {
        double b = bytes;
        string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }

    public static string Stamp(DateTimeOffset t) => t.ToLocalTime().ToString("yyyyMMdd_HHmm");
}

/// <summary>Подключение к сетевой шаре под указанной учёткой на время операции (WNetAddConnection2).</summary>
public sealed class NetworkShareConnection : IDisposable
{
    private readonly string? _remote;

    public NetworkShareConnection(BackupDestination dest)
    {
        if (string.IsNullOrWhiteSpace(dest.Username) || !dest.Path.StartsWith(@"\\")) return;
        var parts = dest.Path.TrimStart('\\').Split('\\');
        if (parts.Length < 2) return;
        _remote = $@"\\{parts[0]}\{parts[1]}";
        var nr = new NetResource { Scope = 2, Type = 1, DisplayType = 3, Usage = 1, RemoteName = _remote };
        var rc = WNetAddConnection2(nr, dest.Password, dest.Username, 0);
        if (rc != 0 && rc != 1219 /* уже подключено под другой учёткой */)
            throw new IOException($"Не удалось подключиться к {_remote} под {dest.Username}: код {rc}");
    }

    public void Dispose()
    {
        if (_remote is not null) WNetCancelConnection2(_remote, 0, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private class NetResource
    {
        public int Scope; public int Type; public int DisplayType; public int Usage;
        public string? LocalName; public string? RemoteName; public string? Comment; public string? Provider;
    }

    [DllImport("mpr.dll")] private static extern int WNetAddConnection2(NetResource netResource, string? password, string? username, int flags);
    [DllImport("mpr.dll")] private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
