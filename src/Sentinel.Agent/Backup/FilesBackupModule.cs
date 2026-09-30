using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Modules;

namespace Sentinel.Agent.Backup;

/// <summary>Zip папок и файлов: полный раз в N дней, между ними разностные (изменённые после последнего полного). Опционально через VSS.</summary>
public class FilesBackupModule : BackupModuleBase<FilesBackupSettings>
{
    public FilesBackupModule(ILogger<FilesBackupModule> log) : base(log) { }

    public override string Id => ModuleIds.BackupFiles;
    protected override string[] ArtifactExtensions => new[] { "zip" };

    protected override Task<List<Artifact>> CreateArtifactsAsync(ModuleContext ctx, FilesBackupSettings s, string slug, string destination, BackupLog log, CancellationToken ct)
    {
        if (s.Sources.Count == 0) throw new InvalidOperationException("Не заданы источники.");
        var artifact = string.Equals(s.Mode, "mirror", StringComparison.OrdinalIgnoreCase)
            ? MirrorSources(s, slug, destination, log, ct)
            : ZipSources(ctx.Check.Id, s.Sources, s.Exclude, s.UseVss, s.FullEveryDays, slug, null, destination, log, ct);
        return Task.FromResult(new List<Artifact> { artifact });
    }

    /// <summary>
    /// Режим «зеркало»: в назначении держим актуальную копию исходных папок — копируются только изменившиеся файлы.
    /// Подходит для больших рабочих папок, где нужна просто свежая копия, а не история версий.
    /// </summary>
    private Artifact MirrorSources(FilesBackupSettings s, string slug, string destination, BackupLog log, CancellationToken ct)
    {
        var excludeRx = s.Exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(WildcardToRegex).ToList();
        var mirrorRoot = Path.Combine(destination, slug);
        var snapshots = new Dictionary<string, VssSnapshot>(StringComparer.OrdinalIgnoreCase);
        var total = new MirrorResult();

        try
        {
            foreach (var source in s.Sources.Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                ct.ThrowIfCancellationRequested();
                var full = BackupUtil.ValidatePath(source, "Источник");
                if (!Directory.Exists(full)) { log.Warn($"Источник не найден: {full}"); continue; }

                var root = full;
                if (s.UseVss && Path.IsPathRooted(full) && !full.StartsWith(@"\\"))
                {
                    var volume = Path.GetPathRoot(full)!.TrimEnd('\\');
                    if (!snapshots.TryGetValue(volume, out var snap))
                    {
                        try { snap = VssSnapshot.Create(volume, log); snapshots[volume] = snap; }
                        catch (Exception ex) { log.Warn($"VSS недоступен ({ex.Message}), копируем с живого тома"); snap = null; }
                    }
                    if (snap is not null) root = snap.Translate(full);
                }

                // Каждый источник — своя подпапка в зеркале, иначе одинаковые имена файлов из разных папок перемешаются.
                // Имя папки сохраняем как в источнике (так копию проще читать), чистим только недопустимые символы.
                var leaf = Path.GetFileName(full.TrimEnd('\\'));
                if (string.IsNullOrEmpty(leaf)) leaf = full.Replace(":", "").Replace("\\", "-").Trim('-');
                foreach (var bad in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(bad, '-');
                var target = Path.Combine(mirrorRoot, leaf);
                log.Info($"Зеркало: {full} → {target}");
                var skipOnError = string.Equals(s.MirrorOnError, "skip", StringComparison.OrdinalIgnoreCase);
                var r = MirrorCopy.Sync(root, target, excludeRx, s.MirrorDeleteRemoved, s.MirrorDeleteAfterDays, skipOnError, s.MirrorRememberSkipped, log, ct);
                total.Merge(r);
            }
        }
        finally { foreach (var snap in snapshots.Values) snap?.Dispose(); }

        log.Info($"Скопировано {total.Copied}, обновлено {total.Updated}, без изменений {total.Unchanged}, удалено {total.Deleted}" +
                 $"{(total.PendingDelete > 0 ? $", ждут удаления {total.PendingDelete} (отсрочка {s.MirrorDeleteAfterDays} дн.)" : "")}" +
                 $"{(total.Skipped > 0 ? $", пропущено {total.Skipped}" : "")}" +
                 $"{(total.Failed > 0 ? $", с ошибками {total.Failed}" : "")}; передано {BackupUtil.HumanSize(total.TransferredBytes)}");
        // Если все пропуски — из журнала прошлых прогонов, новых причин нет и строку печатать незачем.
        if (total.Errors.Count > 0) log.Warn((total.Failed > 0 ? "Причины ошибок: " : "Причины пропуска: ") + total.ErrorSummary());
        else if (total.Skipped > 0) log.Info($"Пропущено по журналу непереносимых: {total.Skipped} (файл в копии .sentinel-mirror-skip.tsv)");
        LogFreeSpace(destination, log);
        if (total.Failed > 0 && total.Copied + total.Updated + total.Unchanged == 0)
            throw new InvalidOperationException($"Не удалось скопировать ни одного файла ({total.Failed}). {total.ErrorSummary()}");

        return new Artifact
        {
            Path = mirrorRoot, Kind = "mirror", IsDirectory = true, SizeBytes = total.TotalBytes,
            Verified = total.Failed == 0 && total.Skipped == 0,
            Error = total.Failed > 0
                ? $"скопировано {total.Copied + total.Updated}, не удалось {total.Failed}. {total.ErrorSummary()}"
                : null,
            Warning = total.Failed == 0 && total.Skipped > 0
                ? $"пропущено файлов: {total.Skipped}. {total.ErrorSummary()}"
                : null,
        };
    }

    /// <summary>Свободное место в назначении — самая частая причина, по которой копия «вдруг» перестала обновляться.</summary>
    private static void LogFreeSpace(string destination, BackupLog log)
    {
        try
        {
            var rootPath = Path.GetPathRoot(destination);
            if (string.IsNullOrEmpty(rootPath) || rootPath!.StartsWith(@"\\")) return;   // на шаре свободное место так не узнать
            var drive = new DriveInfo(rootPath);
            log.Info($"Свободно в назначении: {BackupUtil.HumanSize(drive.AvailableFreeSpace)} из {BackupUtil.HumanSize(drive.TotalSize)}");
        }
        catch { /* не критично */ }
    }

    /// <summary>Общая реализация для files и 1c.file.</summary>
    protected Artifact ZipSources(Guid checkId, List<string> sources, List<string> exclude, bool useVss, int fullEveryDays, string slug, string? target, string destination, BackupLog log, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var lastFull = LastFull(destination, Prefix(slug, target));
        var kind = lastFull is null || fullEveryDays <= 1 || now - lastFull.Value.at >= TimeSpan.FromDays(Math.Max(1, fullEveryDays)) ? "full" : "diff";
        // Имя файла хранит время с точностью до минуты; точный момент старта полного бэкапа берём из состояния агента, если он о том же файле.
        var state = RunStateStore.Get(checkId);
        var since = kind == "diff"
            ? (state.LastFull is not null && state.LastFullArtifact == lastFull!.Value.path ? state.LastFull.Value.UtcDateTime : lastFull!.Value.at.UtcDateTime)
            : DateTime.MinValue;
        log.Info(kind == "full" ? "Полный бэкап" : $"Разностный бэкап: файлы, изменённые после {lastFull!.Value.at:dd.MM.yyyy HH:mm}");

        var excludeRx = exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(WildcardToRegex).ToList();
        var outPath = Path.Combine(destination, ArtifactName(slug, target, kind, "zip", now));
        var tmpPath = outPath + ".tmp";
        var snapshots = new Dictionary<string, VssSnapshot>(StringComparer.OrdinalIgnoreCase);
        long files = 0, skipped = 0, bytes = 0;

        try
        {
            using (var zipStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                foreach (var source in sources.Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    ct.ThrowIfCancellationRequested();
                    var full = BackupUtil.ValidatePath(source, "Источник");
                    var root = full;
                    if (useVss && Path.IsPathRooted(full) && !full.StartsWith(@"\\"))
                    {
                        var volume = Path.GetPathRoot(full)!.TrimEnd('\\');
                        if (!snapshots.TryGetValue(volume, out var snap))
                        {
                            try { snap = VssSnapshot.Create(volume, log); snapshots[volume] = snap; }
                            catch (Exception ex) { log.Warn($"VSS недоступен ({ex.Message}), копируем с живого тома"); snap = null; }
                        }
                        if (snap is not null) root = snap.Translate(full);
                    }

                    if (File.Exists(root))
                    {
                        AddFile(zip, root, Path.GetFileName(full), since, excludeRx, ref files, ref skipped, ref bytes, log);
                    }
                    else if (Directory.Exists(root))
                    {
                        var baseName = Path.GetFileName(full.TrimEnd('\\'));
                        foreach (var f in EnumerateFilesSafe(root, log))
                        {
                            ct.ThrowIfCancellationRequested();
                            var rel = Path.Combine(baseName, f.Substring(root.Length).TrimStart('\\'));
                            AddFile(zip, f, rel, since, excludeRx, ref files, ref skipped, ref bytes, log);
                        }
                    }
                    else
                    {
                        throw new FileNotFoundException($"Источник не найден: {source}");
                    }
                }
            }

            if (kind == "diff" && files == 0)
            {
                log.Info("Изменённых файлов нет — разностный архив пуст");
            }
            File.Move(tmpPath, outPath);
            if (kind == "full") RunStateStore.Update(checkId, e => { e.LastFull = now; e.LastFullArtifact = outPath; });
            log.Info($"В архиве {files} файлов ({BackupUtil.HumanSize(bytes)} до сжатия), пропущено {skipped}");
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
        finally
        {
            foreach (var snap in snapshots.Values) snap.Dispose();
        }

        // Что было полным, а что разностным, выводится из имён файлов в назначении — отдельного состояния нет.
        var verified = VerifyZip(outPath, files, log);
        return new Artifact { Path = outPath, Kind = kind, Target = target, Verified = verified };
    }

    private static void AddFile(ZipArchive zip, string file, string entryName, DateTime since, List<Regex> exclude, ref long files, ref long skipped, ref long bytes, BackupLog log)
    {
        if (exclude.Any(rx => rx.IsMatch(file) || rx.IsMatch(entryName))) { skipped++; return; }
        FileInfo fi;
        try { fi = new FileInfo(file); }
        catch { skipped++; return; }
        if (since != DateTime.MinValue && fi.LastWriteTimeUtc <= since) { skipped++; return; }

        try
        {
            var entry = zip.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
            entry.LastWriteTime = fi.LastWriteTime;
            using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            using (var dst = entry.Open())
                src.CopyTo(dst);
            files++;
            bytes += fi.Length;
        }
        catch (Exception ex)
        {
            skipped++;
            log.Warn($"Пропущен {entryName}: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, BackupLog log)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subs = Array.Empty<string>(), files = Array.Empty<string>();
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch (Exception ex) { log.Warn($"Нет доступа к {dir}: {ex.Message}"); continue; }
            foreach (var s in subs) stack.Push(s);
            foreach (var f in files) yield return f;
        }
    }

    protected static bool VerifyZip(string path, long expectedFiles, BackupLog log)
    {
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var count = zip.Entries.Count;
                if (count != expectedFiles) { log.Warn($"Проверка архива: записей {count}, ожидалось {expectedFiles}"); return false; }
            }
            return true;
        }
        catch (Exception ex) { log.Warn("Архив не читается: " + ex.Message); return false; }
    }

    private static Regex WildcardToRegex(string pattern)
        => new Regex(Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", "."), RegexOptions.IgnoreCase);
}

/// <summary>Файловая база 1С: 1Cv8.1CD (или вся папка) через VSS; проверка заголовка файла базы.</summary>
public sealed class OneCFileBackupModule : FilesBackupModule
{
    public OneCFileBackupModule(ILogger<OneCFileBackupModule> log) : base(log) { }

    public override string Id => ModuleIds.Backup1CFile;

    protected override Task<List<Artifact>> CreateArtifactsAsync(ModuleContext ctx, FilesBackupSettings _, string slug, string destination, BackupLog log, CancellationToken ct)
    {
        var s = ctx.Settings<OneCFileBackupSettings>();
        if (string.IsNullOrWhiteSpace(s.BasePath)) throw new InvalidOperationException("Не задана папка базы 1С.");
        var db = Path.Combine(s.BasePath, "1Cv8.1CD");
        if (!File.Exists(db)) throw new FileNotFoundException($"Файл базы не найден: {db}");

        // Заголовок файловой базы 1С 8.x начинается с "1CDBMSV8".
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var head = new byte[8];
            var n = fs.Read(head, 0, 8);
            var sig = System.Text.Encoding.ASCII.GetString(head, 0, n);
            if (sig != "1CDBMSV8") throw new InvalidDataException($"1Cv8.1CD не похож на базу 1С (заголовок «{sig}»)");
            log.Info($"База 1С: {BackupUtil.HumanSize(fs.Length)}, заголовок корректен");
        }

        var sources = s.WholeFolder ? new List<string> { s.BasePath } : new List<string> { db };
        var exclude = s.WholeFolder ? new List<string> { "*1Cv8Log*", "*.tmp", "*\\1Cv8tmp.1CD" } : new List<string>();
        var artifact = ZipSources(ctx.Check.Id, sources, exclude, useVss: true, s.FullEveryDays, slug, null, destination, log, ct);
        return Task.FromResult(new List<Artifact> { artifact });
    }
}
