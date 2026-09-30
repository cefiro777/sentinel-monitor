using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>CPU, память, аптайм, ожидающая перезагрузка, дрейф часов относительно сервера.</summary>
public sealed class SystemModule : IModule
{
    public string Id => ModuleIds.System;

    public Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<SystemModuleSettings>();
        var results = new List<CheckResult>();

        // CPU
        try
        {
            var cpu = ReadCpuPercent();
            var status = cpu >= s.CpuCritPercent ? CheckStatus.Critical : cpu >= s.CpuWarnPercent ? CheckStatus.Warning : CheckStatus.Ok;
            var r = ctx.Result("cpu", status, $"Загрузка CPU {cpu:0}%");
            r.Metrics.Add(new MetricSample { Name = "cpu_percent", Value = cpu, Unit = "%" });
            results.Add(r);
        }
        catch (Exception ex) { results.Add(ctx.Result("cpu", CheckStatus.Unknown, "Не удалось прочитать CPU: " + ex.Message)); }

        // Память
        try
        {
            var (totalKb, freeKb) = ReadMemory();
            var usedPct = totalKb == 0 ? 0 : 100.0 * (totalKb - freeKb) / totalKb;
            var status = usedPct >= s.MemoryCritPercent ? CheckStatus.Critical : usedPct >= s.MemoryWarnPercent ? CheckStatus.Warning : CheckStatus.Ok;
            var r = ctx.Result("memory", status, $"Память {usedPct:0}% (свободно {freeKb / 1024.0 / 1024:0.0} ГБ из {totalKb / 1024.0 / 1024:0.0} ГБ)");
            r.Metrics.Add(new MetricSample { Name = "memory_used_percent", Value = usedPct, Unit = "%" });
            r.Metrics.Add(new MetricSample { Name = "memory_free_mb", Value = freeKb / 1024.0, Unit = "MB" });
            results.Add(r);
        }
        catch (Exception ex) { results.Add(ctx.Result("memory", CheckStatus.Unknown, "Не удалось прочитать память: " + ex.Message)); }

        // Аптайм
        var uptime = TimeSpan.FromMilliseconds(GetTickCount64());
        var up = ctx.Result("uptime", CheckStatus.Ok, $"Аптайм {FormatUptime(uptime)}", new
        {
            os = Environment.OSVersion.VersionString,
            is64 = Environment.Is64BitOperatingSystem,
            machine = Environment.MachineName,
            bootAt = DateTimeOffset.UtcNow - uptime,
        });
        up.Metrics.Add(new MetricSample { Name = "uptime_seconds", Value = uptime.TotalSeconds, Unit = "s" });
        results.Add(up);

        // Перезагрузка
        if (s.WarnOnPendingReboot)
        {
            var reasons = PendingRebootReasons();
            results.Add(reasons.Count == 0
                ? ctx.Result("reboot", CheckStatus.Ok, "Перезагрузка не требуется")
                : ctx.Result("reboot", CheckStatus.Warning, "Ожидает перезагрузки: " + string.Join(", ", reasons), new { reasons }));
        }

        // Часы
        if (ctx.ClockOffsetMeasuredAt is not null)
        {
            var drift = Math.Abs(ctx.ClockOffset.TotalSeconds);
            var status = drift > s.ClockDriftWarnSeconds ? CheckStatus.Warning : CheckStatus.Ok;
            var r = ctx.Result("clock", status, $"Расхождение часов с сервером {ctx.ClockOffset.TotalSeconds:+0;-0} с");
            r.Metrics.Add(new MetricSample { Name = "clock_offset_seconds", Value = ctx.ClockOffset.TotalSeconds, Unit = "s" });
            results.Add(r);
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    /// <summary>
    /// Загрузка CPU. Основной источник — счётчики производительности; на старых серверах они часто повреждены
    /// (WMI отвечает «Invalid query» — лечится «lodctr /R» и «winmgmt /resyncperf»), тогда берём Win32_Processor.LoadPercentage:
    /// другой провайдер, от счётчиков не зависит, точность чуть ниже (усреднение по процессорам).
    /// </summary>
    private static double ReadCpuPercent()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
            foreach (ManagementObject o in searcher.Get())
                return Convert.ToDouble(o["PercentProcessorTime"]);
        }
        catch (ManagementException) { /* счётчики производительности сломаны — ниже запасной путь */ }

        double sum = 0; var count = 0;
        using (var searcher = new ManagementObjectSearcher("SELECT LoadPercentage FROM Win32_Processor"))
        foreach (ManagementObject o in searcher.Get())
        {
            if (o["LoadPercentage"] is null) continue;
            sum += Convert.ToDouble(o["LoadPercentage"]);
            count++;
        }
        if (count > 0) return Math.Round(sum / count, 1);
        throw new InvalidOperationException("WMI не вернул данных ни по счётчикам производительности, ни по Win32_Processor");
    }

    private static (ulong totalKb, ulong freeKb) ReadMemory()
    {
        using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
        foreach (ManagementObject o in searcher.Get())
            return (Convert.ToUInt64(o["TotalVisibleMemorySize"]), Convert.ToUInt64(o["FreePhysicalMemory"]));
        throw new InvalidOperationException("WMI не вернул данных");
    }

    private static List<string> PendingRebootReasons()
    {
        var reasons = new List<string>();
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is RegistryKey cbs)
            { cbs.Dispose(); reasons.Add("Component Based Servicing"); }
            if (hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is RegistryKey wu)
            { wu.Dispose(); reasons.Add("Windows Update"); }
            using (var sm = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                if (sm?.GetValue("PendingFileRenameOperations") is string[] ops && ops.Length > 0)
                    reasons.Add("PendingFileRenameOperations");
            }
        }
        return reasons;
    }

    private static string FormatUptime(TimeSpan t)
        => t.TotalDays >= 1 ? $"{(int)t.TotalDays} д {t.Hours} ч" : $"{t.Hours} ч {t.Minutes} мин";

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
