using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sentinel.Agent.Backup;

/// <summary>Итог синхронизации зеркала — для отчёта и метрик.</summary>
public sealed class MirrorResult
{
    public long Copied { get; set; }
    public long Updated { get; set; }
    public long Unchanged { get; set; }
    public long Deleted { get; set; }
    /// <summary>Пропали из источника, но ещё лежат в копии — ждут окончания отсрочки.</summary>
    public long PendingDelete { get; set; }
    public long Failed { get; set; }
    /// <summary>Не скопированы, но задание это не валит: политика «пропускать» или файл в журнале пропущенных.</summary>
    public long Skipped { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    /// <summary>Причина ошибки → сколько файлов и пример пути: чтобы в отчёте была суть, а не первые десять строк.</summary>
    public Dictionary<string, (long Count, string Example)> Errors { get; } = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

    public void AddError(string reason, string relPath, bool skipped)
    {
        if (skipped) Skipped++; else Failed++;
        var key = Normalize(reason);
        Errors[key] = Errors.TryGetValue(key, out var e) ? (e.Count + 1, e.Example) : (1, relPath);
    }

    public void Merge(MirrorResult other)
    {
        Copied += other.Copied; Updated += other.Updated; Unchanged += other.Unchanged;
        Deleted += other.Deleted; PendingDelete += other.PendingDelete; Failed += other.Failed; Skipped += other.Skipped;
        TotalBytes += other.TotalBytes; TransferredBytes += other.TransferredBytes;
        foreach (var kv in other.Errors)
            Errors[kv.Key] = Errors.TryGetValue(kv.Key, out var e) ? (e.Count + kv.Value.Count, e.Example) : kv.Value;
    }

    /// <summary>Убираем из текста конкретный путь — иначе каждая ошибка «уникальна» и группировка бессмысленна.</summary>
    private static string Normalize(string reason)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(reason ?? "", "\"[^\"]*\"", "…").Trim();
        return text.Length == 0 ? "неизвестная ошибка" : text;
    }

    /// <summary>Короткая сводка по причинам для отчёта: «Отказано в доступе — 3955 файлов (пример: …)».</summary>
    public string ErrorSummary()
    {
        if (Errors.Count == 0) return "";
        var top = Errors.OrderByDescending(e => e.Value.Count).Take(3)
            .Select(e => $"{e.Key.TrimEnd('.')} — {e.Value.Count} файл(ов), напр. {e.Value.Example}");
        return string.Join("; ", top);
    }
}

/// <summary>
/// Зеркальная копия папок: копируются только изменившиеся файлы (по размеру и времени изменения),
/// при DeleteRemoved удаляются файлы, которых больше нет в источнике. Аналог robocopy /MIR.
/// </summary>
public static class MirrorCopy
{
    /// <summary>Допуск по времени: FAT/сетевые шары округляют mtime до 2 секунд.</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Журнал отложенных удалений внутри копии: когда файл впервые пропал из источника.</summary>
    private const string StateFile = ".sentinel-mirror.tsv";
    /// <summary>Журнал непереносимых файлов: путь, размер и время источника — пока не изменятся, файл не пробуем копировать.</summary>
    private const string SkipFile = ".sentinel-mirror-skip.tsv";

    /// <param name="deleteAfterDays">Сколько дней держать пропавший файл перед удалением (0 — удалять сразу).</param>
    /// <param name="skipOnError">Не валить задание из-за непереносимых файлов.</param>
    /// <param name="rememberSkipped">Запоминать непереносимые файлы и не пробовать их снова, пока не изменятся.</param>
    public static MirrorResult Sync(string sourceRoot, string targetRoot, List<Regex> exclude, bool deleteRemoved, int deleteAfterDays,
        bool skipOnError, bool rememberSkipped, BackupLog log, CancellationToken ct)
    {
        var result = new MirrorResult();
        Fs.CreateDirectory(targetRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownBad = rememberSkipped ? LoadSkipped(targetRoot, log) : new Dictionary<string, (long Size, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        var stillBad = new Dictionary<string, (long Size, DateTime Time)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateFiles(sourceRoot, log))
        {
            ct.ThrowIfCancellationRequested();
            var rel = file.Substring(sourceRoot.Length).TrimStart('\\', '/');
            if (exclude.Any(rx => rx.IsMatch(rel) || rx.IsMatch(Path.GetFileName(rel)))) continue;
            seen.Add(rel);

            var target = Path.Combine(targetRoot, rel);
            try
            {
                var src = Fs.GetInfo(file);
                if (!src.Exists) continue;                 // файл исчез между обходом и копированием — не ошибка
                result.TotalBytes += src.Length;

                // Файл уже помечен как непереносимый и с тех пор не менялся — не тратим время и не шумим.
                if (knownBad.TryGetValue(rel, out var bad) && bad.Size == src.Length && (bad.Time - src.LastWriteTimeUtc).Duration() <= TimeTolerance)
                {
                    result.Skipped++;
                    stillBad[rel] = (src.Length, src.LastWriteTimeUtc);
                    continue;
                }

                var dst = Fs.GetInfo(target);
                if (dst.Exists && dst.Length == src.Length && (dst.LastWriteTimeUtc - src.LastWriteTimeUtc).Duration() <= TimeTolerance)
                {
                    result.Unchanged++;
                    continue;
                }

                Fs.CreateDirectory(Path.GetDirectoryName(target)!);
                var existed = dst.Exists;
                // Снимаем атрибуты: read-only и hidden ломают перезапись копии.
                if (existed) { try { Fs.SetNormal(target); } catch { } }
                try { Fs.Copy(file, target, overwrite: true); }
                catch (Exception) when (existed)
                {
                    // Частый случай на шаре: чужой файл нельзя перезаписать, но можно удалить и создать заново.
                    Fs.Delete(target);
                    Fs.Copy(file, target, overwrite: false);
                }
                Fs.SetLastWriteTimeUtc(target, src.LastWriteTimeUtc);
                result.TransferredBytes += src.Length;
                if (existed) result.Updated++; else result.Copied++;
            }
            catch (Exception ex)
            {
                result.AddError(ex.Message, rel, skipOnError);
                if (result.Failed + result.Skipped <= 10) log.Warn($"{rel}: {ex.Message}");
                if (rememberSkipped && skipOnError)
                {
                    var fi = Fs.GetInfo(file);
                    if (fi.Exists) stillBad[rel] = (fi.Length, fi.LastWriteTimeUtc);
                }
            }
        }

        if (rememberSkipped) SaveSkipped(targetRoot, stillBad, log);

        if (deleteRemoved) DeleteExtra(targetRoot, seen, result, Math.Max(0, deleteAfterDays), log, ct);
        return result;
    }

    /// <summary>
    /// Убирает из зеркала лишнее. При отсрочке файл не удаляется сразу: запоминаем, когда он пропал из источника,
    /// и удаляем, только когда отсрочка вышла. Вернулся в источник — запись из журнала уходит.
    /// </summary>
    private static void DeleteExtra(string targetRoot, HashSet<string> seen, MirrorResult result, int deleteAfterDays, BackupLog log, CancellationToken ct)
    {
        var pending = LoadPending(targetRoot, log);
        var now = DateTime.UtcNow;
        var stillMissing = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateFiles(targetRoot, log))
        {
            ct.ThrowIfCancellationRequested();
            var rel = file.Substring(targetRoot.Length).TrimStart('\\', '/');
            if (rel.Equals(StateFile, StringComparison.OrdinalIgnoreCase) || rel.Equals(SkipFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Contains(rel)) continue;

            if (deleteAfterDays > 0)
            {
                var missingSince = pending.TryGetValue(rel, out var at) ? at : now;
                if (now - missingSince < TimeSpan.FromDays(deleteAfterDays))
                {
                    stillMissing[rel] = missingSince;
                    result.PendingDelete++;
                    continue;
                }
            }

            try
            {
                try { Fs.SetNormal(file); } catch { }
                Fs.Delete(file);
                result.Deleted++;
            }
            catch (Exception ex) { log.Warn($"Удаление {rel}: {ex.Message}"); stillMissing[rel] = pending.TryGetValue(rel, out var at2) ? at2 : now; }
        }

        SavePending(targetRoot, stillMissing, log);

        // Пустые каталоги — после удаления файлов, начиная с самых глубоких.
        foreach (var dir in SafeDirectories(targetRoot, log).OrderByDescending(d => d.Length))
        {
            try
            {
                var inside = new List<string>();
                Fs.List(dir, inside, inside);
                if (inside.Count == 0) Directory.Delete(dir);
            }
            catch { /* каталог занят — не страшно */ }
        }
    }

    /// <summary>Журнал непереносимых: относительный путь → размер и время источника на момент неудачи.</summary>
    private static Dictionary<string, (long Size, DateTime Time)> LoadSkipped(string targetRoot, BackupLog log)
    {
        var map = new Dictionary<string, (long Size, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(targetRoot, SkipFile);
        if (!Fs.GetInfo(path).Exists) return map;
        try
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var parts = raw.TrimStart('\uFEFF').Split('\t');
                if (parts.Length < 3) continue;
                if (long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                    && DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                    map[parts[2]] = (size, at);
            }
        }
        catch (Exception ex) { log.Warn($"Журнал пропущенных нечитаем ({ex.Message})"); }
        return map;
    }

    private static void SaveSkipped(string targetRoot, Dictionary<string, (long Size, DateTime Time)> skipped, BackupLog log)
    {
        var path = Path.Combine(targetRoot, SkipFile);
        try
        {
            if (skipped.Count == 0)
            {
                if (Fs.GetInfo(path).Exists) { Fs.SetNormal(path); Fs.Delete(path); }
                return;
            }
            var lines = skipped.Select(kv => kv.Value.Size.ToString(CultureInfo.InvariantCulture) + "\t" + kv.Value.Time.ToString("o", CultureInfo.InvariantCulture) + "\t" + kv.Key);
            if (Fs.GetInfo(path).Exists) Fs.SetNormal(path);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            Fs.SetAttributes(path, FileAttributes.Hidden);
        }
        catch (Exception ex) { log.Warn($"Не удалось сохранить журнал пропущенных: {ex.Message}"); }
    }

    /// <summary>Журнал отсрочек: относительный путь → когда файл впервые не нашёлся в источнике (UTC).</summary>
    private static Dictionary<string, DateTime> LoadPending(string targetRoot, BackupLog log)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(targetRoot, StateFile);
        if (!File.Exists(path)) return map;
        try
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.TrimStart('﻿');   // BOM в начале файла иначе ломает разбор первой строки
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (DateTime.TryParse(line.Substring(0, tab), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                    map[line.Substring(tab + 1)] = at;
            }
        }
        catch (Exception ex) { log.Warn($"Журнал отсрочек нечитаем ({ex.Message}) — отсчёт начнётся заново"); }
        return map;
    }

    private static void SavePending(string targetRoot, Dictionary<string, DateTime> pending, BackupLog log)
    {
        var path = Path.Combine(targetRoot, StateFile);
        try
        {
            if (pending.Count == 0)
            {
                if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                return;
            }
            var lines = pending.Select(kv => kv.Value.ToString("o", CultureInfo.InvariantCulture) + "\t" + kv.Key);
            // Windows не даёт перезаписать скрытый файл — снимаем атрибут на время записи.
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetAttributes(path, FileAttributes.Hidden);
        }
        catch (Exception ex) { log.Warn($"Не удалось сохранить журнал отсрочек: {ex.Message}"); }
    }

    /// <summary>Обход без падения на папках, к которым нет доступа.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, BackupLog log)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var files = new List<string>();
            var subdirs = new List<string>();
            try { Fs.List(dir, files, subdirs); }
            catch (Exception ex) { log.Warn($"Пропуск {dir}: {ex.Message}"); continue; }
            foreach (var f in files) yield return f;
            foreach (var sub in subdirs) stack.Push(sub);
        }
    }

    private static IEnumerable<string> SafeDirectories(string root, BackupLog log)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var subs = new List<string>();
            try { Fs.List(dir, new List<string>(), subs); }
            catch (Exception ex) { log.Warn($"Пропуск подпапок {dir}: {ex.Message}"); continue; }
            foreach (var sub in subs) { stack.Push(sub); yield return sub; }
        }
    }
}
