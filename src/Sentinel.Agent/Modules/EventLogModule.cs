using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>Критичные события в журналах Windows за окно наблюдения. Один результат на журнал.</summary>
public sealed class EventLogModule : IModule
{
    public string Id => ModuleIds.EventLog;

    public Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<EventLogModuleSettings>();
        var lookback = TimeSpan.FromMinutes(s.LookbackMinutes > 0 ? s.LookbackMinutes : Math.Max(1, ctx.Check.IntervalSeconds / 60.0));
        var levels = s.Levels.Count > 0 ? s.Levels : new List<int> { 1, 2 };
        var results = new List<CheckResult>();

        foreach (var log in s.Logs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var events = Query(log, levels, lookback, s, ct);
                var count = events.Count;
                var status = count >= s.CritCount ? CheckStatus.Critical : count >= s.WarnCount ? CheckStatus.Warning : CheckStatus.Ok;

                var summary = count == 0
                    ? $"{log}: ошибок за {Human(lookback)} нет"
                    : $"{log}: {count} {Plural(count)} за {Human(lookback)} — " + string.Join("; ",
                        events.GroupBy(e => (e.source, e.id)).OrderByDescending(g => g.Count()).Take(3)
                              .Select(g => $"{g.Key.source} ({g.Key.id})" + (g.Count() > 1 ? $" ×{g.Count()}" : "")));

                var r = ctx.Result(log, status, summary, new
                {
                    lookbackMinutes = lookback.TotalMinutes,
                    events = events.Take(s.MaxEventsInDetails).Select(e => new { e.time, e.level, e.source, e.id, e.message }),
                });
                r.Metrics.Add(new MetricSample { Name = "eventlog_errors", Value = count });
                results.Add(r);
            }
            catch (EventLogNotFoundException)
            {
                results.Add(ctx.Result(log, CheckStatus.Unknown, $"Журнал «{log}» не найден"));
            }
            catch (Exception ex)
            {
                results.Add(ctx.Result(log, CheckStatus.Unknown, $"{log}: {ex.Message}"));
            }
        }

        if (results.Count == 0) results.Add(ctx.Result("", CheckStatus.Unknown, "Не задано ни одного журнала"));
        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    private static List<(DateTime time, string level, string source, int id, string message)> Query(
        string log, List<int> levels, TimeSpan lookback, EventLogModuleSettings s, CancellationToken ct)
    {
        var levelExpr = string.Join(" or ", levels.Select(l => $"Level={l}"));
        var xpath = $"*[System[({levelExpr}) and TimeCreated[timediff(@SystemTime) <= {(long)lookback.TotalMilliseconds}]]]";
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };

        var list = new List<(DateTime, string, string, int, string)>();
        using (var reader = new EventLogReader(query))
        {
            EventRecord? rec;
            var scanned = 0;
            while ((rec = reader.ReadEvent()) is not null && scanned++ < 5000)
            {
                ct.ThrowIfCancellationRequested();
                using (rec)
                {
                    var source = rec.ProviderName ?? "";
                    var id = rec.Id;
                    if (s.IncludeSources.Count > 0 && !s.IncludeSources.Any(x => source.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (s.ExcludeSources.Any(x => source.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (s.IncludeEventIds.Count > 0 && !s.IncludeEventIds.Contains(id)) continue;
                    if (s.ExcludeEventIds.Contains(id)) continue;

                    string message;
                    try { message = rec.FormatDescription() ?? ""; }
                    catch { message = "(текст события недоступен)"; }
                    message = Truncate(message.Replace("\r\n", " ").Replace('\n', ' ').Trim(), 400);

                    var level = rec.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => rec.Level?.ToString() ?? "" };
                    list.Add((rec.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, level, source, id, message));
                }
            }
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    private static string Human(TimeSpan t) => t.TotalMinutes < 60 ? $"{t.TotalMinutes:0} мин" : $"{t.TotalHours:0.#} ч";
    private static string Plural(int n) => n % 10 == 1 && n % 100 != 11 ? "ошибка" : n % 10 is >= 2 and <= 4 && n % 100 is < 10 or > 20 ? "ошибки" : "ошибок";
}
