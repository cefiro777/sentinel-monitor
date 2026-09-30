using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Backup;

/// <summary>Один артефакт, созданный заданием (zip, .bak, .dump).</summary>
public sealed class Artifact
{
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "full";   // full | diff | dump | mirror
    public string? Target { get; set; }
    public bool Verified { get; set; }
    public string? Error { get; set; }
    /// <summary>Не ошибка, но и не «всё чисто»: часть файлов пропущена по политике задания.</summary>
    public string? Warning { get; set; }
    /// <summary>Артефакт — папка-зеркало, а не файл: хеш, ретеншн наборов и выгрузка в облако к ней неприменимы.</summary>
    public bool IsDirectory { get; set; }
    /// <summary>Размер для папки-зеркала (для файла считается сам).</summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// Общий конвейер заданий бэкапа: подключение к назначению → создание артефактов (реализация модуля) →
/// хеш → локальный ретеншн → выгрузка в облако с проверкой → отчёт в виде CheckResult с BackupRunInfo.
/// </summary>
public abstract class BackupModuleBase<TSettings> : IScheduledModule where TSettings : BackupJobSettingsBase, new()
{
    protected readonly ILogger Log;
    protected BackupModuleBase(ILogger log) => Log = log;

    public abstract string Id { get; }

    /// <summary>Создать артефакты в <paramref name="destination"/>. Ошибка одного target — в Artifact.Error, не исключением.</summary>
    protected abstract Task<List<Artifact>> CreateArtifactsAsync(ModuleContext ctx, TSettings s, string slug, string destination, BackupLog log, CancellationToken ct);

    /// <summary>Расширения файлов задания — для ретеншна и контроля в облаке.</summary>
    protected abstract string[] ArtifactExtensions { get; }

    public DateTimeOffset? NextRun(CheckConfig check, DateTimeOffset? lastRun, DateTimeOffset now)
    {
        var s = new ModuleContext(check, TimeSpan.Zero, null).Settings<TSettings>();
        return s.Schedule.NextRun(lastRun, now);
    }

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<TSettings>();
        var slug = string.IsNullOrWhiteSpace(s.JobSlug) ? BackupUtil.Slug(ctx.Check.Name) : BackupUtil.Slug(s.JobSlug);
        var log = new BackupLog(Log, slug);
        var started = DateTimeOffset.UtcNow;
        var results = new List<CheckResult>();

        try
        {
            var destination = BackupUtil.ValidatePath(s.Destination.Path, "Папка назначения");
            using (new NetworkShareConnection(s.Destination))
            {
                try { Directory.CreateDirectory(destination); }
                catch (Exception ex) { throw new InvalidOperationException($"Папка назначения «{destination}» недоступна: {ex.Message}"); }
                log.Info($"Назначение: {s.Destination.Path}");

                var artifacts = await CreateArtifactsAsync(ctx, s, slug, s.Destination.Path, log, ct);
                if (artifacts.Count == 0) throw new InvalidOperationException("Задание не создало ни одного артефакта.");

                foreach (var a in artifacts)
                {
                    var run = new BackupRunInfo { Job = slug, Kind = a.Kind, Target = a.Target, StartedAt = started, ArtifactPath = a.Path, Verified = a.Verified, CloudEnabled = s.Cloud.Enabled };
                    var runLog = new BackupLog(Log, slug);
                    runLog.Lines.AddRange(log.Lines);
                    try
                    {
                        if (a.Error is not null) throw new InvalidOperationException(a.Error);
                        if (a.IsDirectory)
                        {
                            // Зеркало: копия, а не набор архивов — хеша и наборов нет, в облако выгружать нечего.
                            run.SizeBytes = a.SizeBytes;
                            runLog.Info($"Зеркало: {BackupUtil.HumanSize(a.SizeBytes)} в {a.Path}");
                            if (a.Warning is not null) { runLog.Warn(a.Warning); run.Error = a.Warning; }
                            if (s.Cloud.Enabled) runLog.Warn("Облако: в режиме «зеркало» выгрузка не выполняется — заведите отдельное zip-задание для облачной копии.");
                        }
                        else
                        {
                            var fi = new FileInfo(a.Path);
                            run.SizeBytes = fi.Length;
                            run.Sha256 = BackupUtil.Sha256File(a.Path);
                            runLog.Info($"{Path.GetFileName(a.Path)}: {BackupUtil.HumanSize(fi.Length)}, sha256 {run.Sha256.Substring(0, 12)}…{(a.Verified ? ", проверен" : "")}");

                            ApplyLocalRetention(s, slug, a.Target, runLog);

                            if (s.Cloud.Enabled)
                            {
                                try { run.CloudPath = await UploadToCloudAsync(ctx, s, slug, a, run.Sha256, runLog, ct); run.CloudUploaded = true; }
                                catch (Exception ex) when (ex is not OperationCanceledException) { runLog.Warn("Облако: " + ex.Message); run.Error = "Облако: " + ex.Message; }
                            }
                        }
                        run.Success = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        run.Success = false;
                        run.Error = ex.Message;
                        runLog.Warn(ex.Message);
                    }
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.Log = runLog.Lines.ToList();
                    results.Add(ToResult(ctx, run, s));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn(ex.Message);
            var run = new BackupRunInfo { Job = slug, Kind = "full", StartedAt = started, FinishedAt = DateTimeOffset.UtcNow, Success = false, Error = ex.Message, CloudEnabled = s.Cloud.Enabled, Log = log.Lines.ToList() };
            results.Add(ToResult(ctx, run, s));
        }

        RunStateStore.Update(ctx.Check.Id, e => e.LastRun = DateTimeOffset.UtcNow);
        return results;
    }

    private static CheckResult ToResult(ModuleContext ctx, BackupRunInfo run, TSettings s)
    {
        var key = run.Target ?? "";
        var duration = run.FinishedAt - run.StartedAt;
        CheckStatus status;
        string summary;
        if (!run.Success)
        {
            status = CheckStatus.Critical;
            summary = $"Бэкап не выполнен: {run.Error}";
        }
        else if (s.Cloud.Enabled && !run.CloudUploaded)
        {
            status = CheckStatus.Warning;
            summary = $"Бэкап {run.Kind} создан ({BackupUtil.HumanSize(run.SizeBytes)}), но не выгружен в облако: {run.Error}";
        }
        else
        {
            status = CheckStatus.Ok;
            summary = $"Бэкап {run.Kind} выполнен: {BackupUtil.HumanSize(run.SizeBytes)} за {duration.TotalMinutes:0.#} мин" +
                      (run.Verified ? ", проверен" : "") + (run.CloudUploaded ? ", в облаке" : "");
        }
        var r = ctx.Result(key, status, summary, new { run });
        r.Metrics.Add(new MetricSample { Name = "backup_size_bytes", Value = run.SizeBytes, Unit = "B" });
        r.Metrics.Add(new MetricSample { Name = "backup_duration_s", Value = Math.Round(duration.TotalSeconds), Unit = "s" });
        return r;
    }

    // ---------- имена файлов и ретеншн ----------

    /// <summary>{slug}[_{target}]_{yyyyMMdd_HHmm}_{kind}.{ext}</summary>
    protected static string ArtifactName(string slug, string? target, string kind, string ext, DateTimeOffset at)
        => $"{slug}{(string.IsNullOrEmpty(target) ? "" : "_" + BackupUtil.Slug(target!))}_{BackupUtil.Stamp(at)}_{kind}.{ext}";

    private static readonly Regex NameRx = new Regex(@"^(?<prefix>.+)_(?<stamp>\d{8}_\d{4})_(?<kind>full|diff|dump)\.(?<ext>\w+)$", RegexOptions.IgnoreCase);

    protected string Prefix(string slug, string? target) => slug + (string.IsNullOrEmpty(target) ? "" : "_" + BackupUtil.Slug(target!));

    /// <summary>Файлы задания в папке, отсортированные по времени в имени.</summary>
    protected List<(string path, string stamp, string kind)> ListArtifacts(string folder, string prefix)
    {
        var list = new List<(string, string, string)>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var m = NameRx.Match(Path.GetFileName(f));
            if (!m.Success || !string.Equals(m.Groups["prefix"].Value, prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!ArtifactExtensions.Contains(m.Groups["ext"].Value.ToLowerInvariant())) continue;
            list.Add((f, m.Groups["stamp"].Value, m.Groups["kind"].Value.ToLowerInvariant()));
        }
        return list.OrderBy(x => x.Item2).ToList();
    }

    /// <summary>Последний полный артефакт (для решения full/diff и как база разностного).</summary>
    protected (string path, DateTimeOffset at)? LastFull(string folder, string prefix)
    {
        var last = ListArtifacts(folder, prefix).LastOrDefault(x => x.kind == "full");
        if (last.path is null) return null;
        var at = DateTime.ParseExact(last.stamp, "yyyyMMdd_HHmm", null);
        return (last.path, new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at)));
    }

    /// <summary>Оставляем последние N наборов: набор = полный (или dump) + его разностные.</summary>
    private void ApplyLocalRetention(TSettings s, string slug, string? target, BackupLog log)
    {
        var keep = Math.Max(1, s.Retention.KeepFullSets);
        var files = ListArtifacts(s.Destination.Path, Prefix(slug, target));
        var sets = new List<List<string>>();
        foreach (var f in files)
        {
            if (f.kind != "diff" || sets.Count == 0) sets.Add(new List<string>());
            sets[sets.Count - 1].Add(f.path);
        }
        var toDelete = sets.Take(Math.Max(0, sets.Count - keep)).SelectMany(x => x).ToList();
        foreach (var f in toDelete)
        {
            try { File.Delete(f); log.Info($"Ретеншн: удалён {Path.GetFileName(f)}"); }
            catch (Exception ex) { log.Warn($"Ретеншн: не удалось удалить {Path.GetFileName(f)}: {ex.Message}"); }
        }
    }

    // ---------- облако ----------

    private async Task<string> UploadToCloudAsync(ModuleContext ctx, TSettings s, string slug, Artifact a, string sha256, BackupLog log, CancellationToken ct)
    {
        if (!string.Equals(s.Cloud.Provider, "yandex", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"Облако «{s.Cloud.Provider}» не поддерживается.");
        if (string.IsNullOrWhiteSpace(s.Cloud.Token)) throw new InvalidOperationException("не задан OAuth-токен Яндекс.Диска.");

        var folder = $"{s.Cloud.RootFolder.TrimEnd('/')}/{Safe(ctx.TenantSlug)}/{Safe(ctx.HostName)}/{slug}";
        var remote = $"{folder}/{Path.GetFileName(a.Path)}";
        using (var disk = new YandexDiskClient(s.Cloud.Token))
        {
            var quota = await disk.GetQuotaAsync(ct);
            var size = new FileInfo(a.Path).Length;
            log.Info($"Яндекс.Диск: свободно {BackupUtil.HumanSize(quota.Free)} из {BackupUtil.HumanSize(quota.Total)}");
            if (quota.Free < size) throw new IOException($"на Диске мало места: нужно {BackupUtil.HumanSize(size)}, свободно {BackupUtil.HumanSize(quota.Free)}");

            await disk.EnsureFolderAsync(folder, ct);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await disk.UploadAsync(a.Path, remote, ct);
            log.Info($"Выгружено в {remote} за {sw.Elapsed.TotalMinutes:0.#} мин");

            if (s.Cloud.VerifyHash)
            {
                YandexDiskClient.Item? item = null;
                for (var i = 0; i < 10 && item?.Sha256 is null; i++) { if (i > 0) await Task.Delay(3000, ct); item = await disk.GetItemAsync(remote, ct); }
                if (item is null) throw new IOException("после загрузки файл не найден на Диске");
                if (item.Size != size) throw new IOException($"размер на Диске {item.Size} ≠ локальному {size}");
                if (item.Sha256 is not null && !string.Equals(item.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("хеш на Диске не совпал с локальным");
                log.Info("Хеш в облаке совпал");
            }

            // Ретеншн в облаке: по числу файлов задания (таймстемп в имени задаёт порядок).
            if (s.Retention.CloudKeepCount > 0)
            {
                var prefix = Prefix(slug, a.Target);
                var items = (await disk.ListAsync(folder, ct)).Where(i => !i.IsDir && i.Name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Name).ToList();
                foreach (var old in items.Take(Math.Max(0, items.Count - s.Retention.CloudKeepCount)))
                {
                    await disk.DeleteAsync(old.Path, ct);
                    log.Info($"Облако, ретеншн: {old.Name} → корзина");
                }
            }

            if (s.Cloud.WarnFreeGb > 0 && quota.Free - size < s.Cloud.WarnFreeGb * 1024 * 1024 * 1024)
                log.Warn($"На Яндекс.Диске остаётся меньше {s.Cloud.WarnFreeGb} ГБ");
        }
        return remote;
    }

    private static string Safe(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : BackupUtil.Slug(s);
}
