using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Backup;
using Sentinel.Agent.Infrastructure;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Services;

/// <summary>
/// Запускает каждый экземпляр проверки по своему интервалу. Результаты складываются в офлайн-буфер.
/// При смене конфигурации набор задач пересобирается.
/// </summary>
public sealed class CheckScheduler : IDisposable
{
    private readonly ModuleRegistry _modules;
    private readonly ResultQueue _queue;
    private readonly ConfigStore _config;
    private readonly Func<ServerClient?> _server;
    private readonly ILogger<CheckScheduler> _log;

    private readonly object _lock = new object();
    private readonly Dictionary<Guid, CancellationTokenSource> _tasks = new Dictionary<Guid, CancellationTokenSource>();
    private CancellationToken _stopping;

    public CheckScheduler(ModuleRegistry modules, ResultQueue queue, ConfigStore config, Func<ServerClient?> server, ILogger<CheckScheduler> log)
    {
        _modules = modules;
        _queue = queue;
        _config = config;
        _server = server;
        _log = log;
    }

    public void Start(CancellationToken stopping)
    {
        _stopping = stopping;
        _config.Changed += _ => Rebuild();
        Rebuild();
    }

    private void Rebuild()
    {
        var doc = _config.Current;
        lock (_lock)
        {
            foreach (var cts in _tasks.Values) cts.Cancel();
            _tasks.Clear();

            foreach (var check in doc.Checks)
            {
                if (!check.Enabled) continue;
                var module = _modules.Find(check.ModuleId);
                if (module is null)
                {
                    _log.LogWarning("Проверка «{Name}»: модуль {Module} не поддерживается этой версией агента", check.Name, check.ModuleId);
                    _queue.Enqueue(new[]
                    {
                        new CheckResult
                        {
                            CheckId = check.Id, ModuleId = check.ModuleId, Key = "", Status = CheckStatus.Unknown,
                            Summary = $"Модуль {check.ModuleId} не поддерживается агентом v{AgentPaths.Version}", At = DateTimeOffset.UtcNow,
                        },
                    });
                    continue;
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                _tasks[check.Id] = cts;
                if (module is IScheduledModule scheduled)
                    _ = Task.Run(() => ScheduledLoopAsync(scheduled, check, cts.Token), cts.Token);
                else
                    _ = Task.Run(() => LoopAsync(module, check, cts.Token), cts.Token);
            }
        }
        _log.LogInformation("Конфигурация v{Version}: активных проверок {Count}", doc.Version, _tasks.Count);
    }

    /// <summary>Запуск по расписанию (бэкапы): ждём следующего слота, выполняем, запоминаем время.</summary>
    private async Task ScheduledLoopAsync(IScheduledModule module, CheckConfig check, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var lastRun = RunStateStore.Get(check.Id).LastRun;
            var next = module.NextRun(check, lastRun, DateTimeOffset.UtcNow);
            if (next is null) { _log.LogWarning("Задание «{Name}»: расписание не задано", check.Name); return; }

            var wait = next.Value - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _log.LogInformation("Задание «{Name}»: следующий запуск {Next:dd.MM HH:mm}", check.Name, next.Value.ToLocalTime());
                // Спим кусками: смена конфига отменит токен, а длинный Task.Delay ограничен ~24 сутками.
                try { await Task.Delay(wait > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : wait, ct); } catch (OperationCanceledException) { return; }
                if (DateTimeOffset.UtcNow < next.Value) continue;
            }

            await RunOnceAsync(module, check, ct);
        }
    }

    private async Task RunOnceAsync(IModule module, CheckConfig check, CancellationToken ct)
    {
        _log.LogInformation("Задание «{Name}» ({Module}) запущено", check.Name, check.ModuleId);
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromHours(12));
                var results = await module.RunAsync(MakeContext(check), timeout.Token);
                _queue.Enqueue(results);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Задание «{Name}» завершилось ошибкой", check.Name);
            RunStateStore.Update(check.Id, e => e.LastRun = DateTimeOffset.UtcNow);
            _queue.Enqueue(new[]
            {
                new CheckResult { CheckId = check.Id, ModuleId = check.ModuleId, Key = "", Status = CheckStatus.Critical, Summary = "Ошибка задания: " + ex.Message, At = DateTimeOffset.UtcNow },
            });
        }
    }

    private ModuleContext MakeContext(CheckConfig check)
    {
        var server = _server();
        var doc = _config.Current;
        return new ModuleContext(check, server?.ClockOffset ?? TimeSpan.Zero, server?.ClockOffsetMeasuredAt)
        {
            TenantSlug = doc.TenantSlug,
            HostName = string.IsNullOrEmpty(doc.HostName) ? Environment.MachineName : doc.HostName,
        };
    }

    private async Task LoopAsync(IModule module, CheckConfig check, CancellationToken ct)
    {
        // Небольшой случайный сдвиг, чтобы все проверки не стартовали одновременно.
        try { await Task.Delay(TimeSpan.FromMilliseconds(new Random(check.Id.GetHashCode()).Next(200, 3000)), ct); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromSeconds(Math.Max(10, check.IntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var results = await module.RunAsync(MakeContext(check), timeout.Token);
                    _queue.Enqueue(results);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Проверка «{Name}» ({Module}) завершилась ошибкой", check.Name, check.ModuleId);
                _queue.Enqueue(new[]
                {
                    new CheckResult
                    {
                        CheckId = check.Id, ModuleId = check.ModuleId, Key = "", Status = CheckStatus.Unknown,
                        Summary = "Ошибка модуля: " + ex.Message, At = DateTimeOffset.UtcNow,
                    },
                });
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var wait = interval - elapsed;
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Внеочередной запуск проверки (например, по команде backup.run).</summary>
    public async Task<IReadOnlyList<CheckResult>?> RunNowAsync(Guid checkId, CancellationToken ct)
    {
        CheckConfig? check = null;
        foreach (var c in _config.Current.Checks) if (c.Id == checkId) { check = c; break; }
        if (check is null) return null;
        var module = _modules.Find(check.ModuleId);
        if (module is null) return null;
        var results = await module.RunAsync(MakeContext(check), ct);
        _queue.Enqueue(results);
        return results;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var cts in _tasks.Values) cts.Cancel();
            _tasks.Clear();
        }
    }
}
