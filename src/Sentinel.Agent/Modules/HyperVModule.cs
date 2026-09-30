using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>
/// Виртуальные машины Hyper-V: состояние, аптайм, службы интеграции (heartbeat), контрольные точки, репликация.
/// Читается через WMI root\virtualization\v2 на самом хосте — отдельные учётки и агенты внутри ВМ не нужны.
/// </summary>
public sealed class HyperVModule : IModule
{
    public string Id => ModuleIds.VmHyperV;

    // Msvm_ComputerSystem.EnabledState
    private static readonly Dictionary<ushort, string> States = new Dictionary<ushort, string>
    {
        { 2, "работает" }, { 3, "выключена" }, { 4, "остановка" }, { 6, "сохранена" },
        { 9, "приостановлена" }, { 10, "запуск" }, { 32773, "пауза" }, { 32768, "пауза" }, { 32769, "приостановлена" },
    };

    public Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<HyperVModuleSettings>();
        var results = new List<CheckResult>();
        var scope = new ManagementScope(@"\\.\root\virtualization\v2");
        try { scope.Connect(); }
        catch (Exception ex)
        {
            return Done(new[] { ctx.Result("", CheckStatus.Unknown,
                "Hyper-V недоступен: " + ex.Message + ". Модуль ставится на хост Hyper-V; служба vmms должна работать, а агент — от LocalSystem.") });
        }

        var include = new HashSet<string>(s.Include.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(s.Exclude.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        var expectedOff = new HashSet<string>(s.ExpectedOff.Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

        var checkpoints = s.CheckpointWarnDays > 0 ? Checkpoints(scope) : new Dictionary<string, List<(string Name, DateTime At)>>();
        var replication = s.CheckReplication ? Replication(scope) : new Dictionary<string, (ushort Health, ushort State)>();
        var found = 0;

        // Виртуальные машины: Msvm_ComputerSystem с Caption 'Virtual Machine' (сам хост имеет другой Caption).
        using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'")))
        using (var vms = searcher.Get())
        {
            foreach (ManagementObject vm in vms)
            {
                ct.ThrowIfCancellationRequested();
                using (vm)
                {
                    var name = Str(vm["ElementName"]);
                    if (name.Length == 0) continue;
                    if (include.Count > 0 && !include.Contains(name)) continue;
                    if (exclude.Contains(name)) continue;
                    found++;

                    var id = Str(vm["Name"]);
                    var state = UShort(vm["EnabledState"]);
                    var stateText = States.TryGetValue(state, out var st) ? st : "состояние " + state;
                    var running = state == 2;
                    var shouldRun = !expectedOff.Contains(name);

                    var problems = new List<string>();
                    var status = CheckStatus.Ok;
                    if (!running && shouldRun)
                    {
                        problems.Add("ВМ " + stateText);
                        status = s.StoppedIsCritical ? CheckStatus.Critical : CheckStatus.Warning;
                    }

                    var summary = new ManagementObject[0];
                    double? uptimeHours = null;
                    string? heartbeat = null;
                    if (running)
                    {
                        uptimeHours = Uptime(scope, id);
                        if (s.CheckHeartbeat)
                        {
                            heartbeat = Heartbeat(scope, id);
                            // "не отвечает" — либо гостевая ОС зависла, либо не установлены службы интеграции.
                            if (heartbeat == "нет ответа" && Worse(ref status, CheckStatus.Warning)) problems.Add("службы интеграции не отвечают (heartbeat)");
                        }
                    }

                    var oldCheckpoints = new List<string>();
                    if (checkpoints.TryGetValue(id, out var snaps))
                    {
                        var limit = DateTime.UtcNow.AddDays(-s.CheckpointWarnDays);
                        foreach (var snap in snaps.Where(x => x.At < limit).OrderBy(x => x.At))
                            oldCheckpoints.Add($"{snap.Name} ({(DateTime.UtcNow - snap.At).TotalDays:0} дн.)");
                        if (oldCheckpoints.Count > 0 && Worse(ref status, CheckStatus.Warning))
                            problems.Add($"контрольных точек старше {s.CheckpointWarnDays} дн.: {oldCheckpoints.Count} — растёт разностный диск");
                    }

                    string? replicationText = null;
                    if (replication.TryGetValue(id, out var rep))
                    {
                        replicationText = ReplicationHealth(rep.Health);
                        if (rep.Health >= 3 && Worse(ref status, CheckStatus.Critical)) problems.Add("репликация: " + replicationText);
                        else if (rep.Health == 2 && Worse(ref status, CheckStatus.Warning)) problems.Add("репликация: " + replicationText);
                    }

                    var parts = new List<string> { stateText };
                    if (uptimeHours is double up) parts.Add(up >= 24 ? $"аптайм {up / 24:0.0} дн." : $"аптайм {up:0.0} ч");
                    if (heartbeat is not null) parts.Add("heartbeat: " + heartbeat);
                    if (checkpoints.TryGetValue(id, out var all) && all.Count > 0) parts.Add($"контрольных точек {all.Count}");
                    if (replicationText is not null) parts.Add("репликация: " + replicationText);

                    var text = status == CheckStatus.Ok
                        ? $"{name}: {string.Join(", ", parts)}"
                        : $"{name}: {string.Join("; ", problems)} [{string.Join(", ", parts)}]";

                    var r = ctx.Result(name, status, text, new
                    {
                        id,
                        state = stateText,
                        running,
                        expectedOff = !shouldRun,
                        uptimeHours = uptimeHours is double u ? Math.Round(u, 1) : (double?)null,
                        heartbeat,
                        checkpoints = all?.Select(x => new { name = x.Name, at = x.At.ToString("yyyy-MM-dd HH:mm") }).ToList(),
                        oldCheckpoints,
                        replication = replicationText,
                    });
                    r.Metrics.Add(new MetricSample { Name = "vm_running", Value = running ? 1 : 0 });
                    if (uptimeHours is double uh) r.Metrics.Add(new MetricSample { Name = "vm_uptime_hours", Value = Math.Round(uh, 1), Unit = "h" });
                    if (all is not null) r.Metrics.Add(new MetricSample { Name = "vm_checkpoints", Value = all.Count });
                    results.Add(r);
                    _ = summary;
                }
            }
        }

        if (found == 0)
            results.Add(ctx.Result("", CheckStatus.Unknown, include.Count > 0
                ? "Ни одна из указанных ВМ не найдена на этом хосте Hyper-V"
                : "На хосте Hyper-V нет виртуальных машин"));
        return Done(results);
    }

    private static Task<IReadOnlyList<CheckResult>> Done(IReadOnlyList<CheckResult> r) => Task.FromResult(r);

    /// <summary>Аптайм из сводки Msvm_SummaryInformation (мс).</summary>
    private static double? Uptime(ManagementScope scope, string vmId)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"SELECT UpTime FROM Msvm_SummaryInformation WHERE Name='{Escape(vmId)}'")))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        var ms = row["UpTime"];
                        if (ms is null) return null;
                        return Convert.ToDouble(ms, CultureInfo.InvariantCulture) / 1000 / 3600;
                    }
        }
        catch { }
        return null;
    }

    /// <summary>Состояние службы Heartbeat внутри гостевой ОС.</summary>
    private static string? Heartbeat(ManagementScope scope, string vmId)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"SELECT OperationalStatus FROM Msvm_HeartbeatComponent WHERE SystemName='{Escape(vmId)}'")))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        var status = row["OperationalStatus"] as ushort[];
                        if (status is null || status.Length == 0) return null;
                        switch (status[0])
                        {
                            case 2: return "ок";
                            case 12: return "нет ответа";                                  // No Contact
                            case 13: return "потерян";                                     // Lost Communication
                            default: return status.Length > 1 && status[1] == 32771 ? "ок (с задержкой)" : "статус " + status[0];
                        }
                    }
        }
        catch { }
        return null;
    }

    /// <summary>Контрольные точки по каждой ВМ: Msvm_VirtualSystemSettingData с типом снимка.</summary>
    private static Dictionary<string, List<(string Name, DateTime At)>> Checkpoints(ManagementScope scope)
    {
        var map = new Dictionary<string, List<(string, DateTime)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // VirtualSystemType: ...Snapshot / ...Snapshot.Realized / ...Snapshot.Recovery
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT ElementName, ConfigurationID, CreationTime, VirtualSystemType FROM Msvm_VirtualSystemSettingData")))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        var type = Str(row["VirtualSystemType"]);
                        if (type.IndexOf("Snapshot", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var vmId = Str(row["ConfigurationID"]);
                        if (vmId.Length == 0) continue;
                        var at = ParseCim(Str(row["CreationTime"]));
                        if (at is null) continue;
                        if (!map.TryGetValue(vmId, out var list)) map[vmId] = list = new List<(string, DateTime)>();
                        list.Add((Str(row["ElementName"]), at.Value));
                    }
        }
        catch { }
        return map;
    }

    /// <summary>Здоровье репликации по ВМ, если она настроена.</summary>
    private static Dictionary<string, (ushort Health, ushort State)> Replication(ManagementScope scope)
    {
        var map = new Dictionary<string, (ushort, ushort)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT ReplicationHealth, ReplicationState, Name FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'")))
            using (var rows = searcher.Get())
                foreach (ManagementObject row in rows)
                    using (row)
                    {
                        var health = UShort(row["ReplicationHealth"]);
                        var state = UShort(row["ReplicationState"]);
                        if (state == 0 || state == 1) continue;   // репликация не настроена
                        map[Str(row["Name"])] = (health, state);
                    }
        }
        catch { }
        return map;
    }

    private static string ReplicationHealth(ushort health)
    {
        switch (health)
        {
            case 0: return "не настроена";
            case 1: return "в норме";
            case 2: return "предупреждение";
            case 3: return "критично";
            default: return "статус " + health;
        }
    }

    /// <summary>Повышает статус до <paramref name="to"/>; true — чтобы использовать в условии вместе с добавлением проблемы.</summary>
    private static bool Worse(ref CheckStatus status, CheckStatus to)
    {
        if (to > status) status = to;
        return true;
    }

    /// <summary>CIM_DATETIME: 20260922183000.000000+420</summary>
    private static DateTime? ParseCim(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14) return null;
        try { return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime(); }
        catch { return null; }
    }

    private static string Str(object? v) => v?.ToString() ?? "";
    private static ushort UShort(object? v) => v is null ? (ushort)0 : Convert.ToUInt16(v, CultureInfo.InvariantCulture);
    private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("'", "\\'");
}
