using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>Свободное место на локальных дисках.</summary>
public sealed class DisksModule : IModule
{
    public string Id => ModuleIds.Disks;

    public Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<DisksModuleSettings>();
        var include = new HashSet<string>(s.Include.Select(Norm), StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(s.Exclude.Select(Norm), StringComparer.OrdinalIgnoreCase);
        var results = new List<CheckResult>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            var key = Norm(drive.Name);
            if (drive.DriveType != DriveType.Fixed) continue;
            if (include.Count > 0 && !include.Contains(key)) continue;
            if (exclude.Contains(key)) continue;
            if (!drive.IsReady) { results.Add(ctx.Result(key, CheckStatus.Unknown, $"{key} не готов")); continue; }

            double total = drive.TotalSize, free = drive.AvailableFreeSpace;
            var freePct = total > 0 ? 100.0 * free / total : 0;
            var freeGb = free / 1024 / 1024 / 1024;
            var totalGb = total / 1024 / 1024 / 1024;

            var status = CheckStatus.Ok;
            if (freePct <= s.CritFreePercent || (s.CritFreeGb > 0 && freeGb <= s.CritFreeGb)) status = CheckStatus.Critical;
            else if (freePct <= s.WarnFreePercent || (s.WarnFreeGb > 0 && freeGb <= s.WarnFreeGb)) status = CheckStatus.Warning;

            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? key : $"{key} ({drive.VolumeLabel})";
            var r = ctx.Result(key, status, $"{label}: свободно {freeGb:0.0} ГБ из {totalGb:0.0} ГБ ({freePct:0}%)",
                new { label = drive.VolumeLabel, fileSystem = drive.DriveFormat, totalGb = Math.Round(totalGb, 1), freeGb = Math.Round(freeGb, 1) });
            r.Metrics.Add(new MetricSample { Name = "disk_free_percent", Value = Math.Round(freePct, 1), Unit = "%" });
            r.Metrics.Add(new MetricSample { Name = "disk_free_gb", Value = Math.Round(freeGb, 2), Unit = "GB" });
            results.Add(r);
        }

        if (results.Count == 0) results.Add(ctx.Result("", CheckStatus.Unknown, "Не найдено ни одного локального диска по заданным фильтрам"));
        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    private static string Norm(string name) => name.Trim().TrimEnd('\\', '/').ToUpperInvariant();
}
