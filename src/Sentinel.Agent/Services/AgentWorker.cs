using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Commands;
using Sentinel.Agent.Infrastructure;
using Sentinel.Agent.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Services;

/// <summary>Главный цикл службы: конфиг → планировщик → канал к серверу → отправка накопленных результатов.</summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentWorker> _log;
    private readonly ModuleRegistry _modules;

    private AgentIdentity _identity = null!;
    private ServerClient _server = null!;
    private ConfigStore _config = null!;
    private ResultQueue _queue = null!;
    private CheckScheduler _scheduler = null!;
    private HubClient _hub = null!;
    private readonly SemaphoreSlim _configLock = new SemaphoreSlim(1, 1);

    private readonly bool _asService;

    public AgentWorker(ILoggerFactory loggerFactory, ModuleRegistry modules, AgentRunMode mode)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<AgentWorker>();
        _modules = modules;
        _asService = mode.AsService;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Sentinel Agent v{Version} запускается ({Machine})", AgentPaths.Version, Environment.MachineName);

        var identity = AgentIdentity.Load();
        if (identity is null || !identity.IsEnrolled)
        {
            _log.LogError("Агент не зарегистрирован. Выполните: SentinelAgent.exe enroll --server <url> --token <token>");
            return;
        }
        _identity = identity;

        _server = new ServerClient(_identity.ServerUrl, _identity, _loggerFactory.CreateLogger<ServerClient>());
        _config = new ConfigStore();
        _queue = new ResultQueue(AgentPaths.QueueDb);
        _log.LogInformation("В офлайн-буфере {Count} результатов", _queue.Count());

        if (_config.LoadCache()) _log.LogInformation("Загружен кэш конфигурации v{Version}", _config.Version);
        await RefreshConfigAsync(ct, force: true);

        _scheduler = new CheckScheduler(_modules, _queue, _config, () => _server, _loggerFactory.CreateLogger<CheckScheduler>());
        _scheduler.Start(ct);

        var updater = new AgentUpdater(_identity, _asService, _loggerFactory.CreateLogger<AgentUpdater>());
        var handlers = new List<ICommandHandler>
        {
            new AgentUpdateHandler(updater),
            new PingHandler(),
            new ConfigRefreshHandler(c => RefreshConfigAsync(c, force: true)),
            new ServiceControlHandler(),
            new PowerHandler(CommandTypes.Reboot),
            new PowerHandler(CommandTypes.Shutdown),
            new ExecHandler(_loggerFactory.CreateLogger<ExecHandler>()),
            new BackupRunHandler((id, c) => _scheduler.RunNowAsync(id, c)),
            new Cctv.CctvCommandHandler(() => _config.Current, _loggerFactory.CreateLogger<Cctv.CctvCommandHandler>()),
        };
        var dispatcher = new CommandDispatcher(_identity, handlers, _loggerFactory.CreateLogger<CommandDispatcher>());

        _hub = new HubClient(_server, _identity, dispatcher, _config,
            (version, c) => version > _config.Version ? RefreshConfigAsync(c, force: true) : Task.CompletedTask,
            () => _queue.Count(), _loggerFactory.CreateLogger<HubClient>());

        var hubTask = _hub.RunAsync(ct);
        var uploadTask = UploadLoopAsync(ct);
        var updateTask = updater.RunLoopAsync(ct);

        await Task.WhenAll(hubTask, uploadTask, updateTask);
        _scheduler.Dispose();
        await _hub.DisposeAsync();
        _queue.Dispose();
        _server.Dispose();
        _log.LogInformation("Агент остановлен");
    }

    /// <summary>Получить конфигурацию с сервера. Возвращает актуальную версию.</summary>
    private async Task<int> RefreshConfigAsync(CancellationToken ct, bool force)
    {
        await _configLock.WaitAsync(ct);
        try
        {
            var doc = await _server.GetConfigAsync(ct);
            if (_config.Apply(doc)) _log.LogInformation("Применена конфигурация v{Version} ({Count} проверок)", doc.Version, doc.Checks.Count);
            return doc.Version;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Не удалось получить конфигурацию: {Error}. Работаем по кэшу v{Version}", ErrorText.Full(ex), _config.Version);
            return _config.Version;
        }
        finally { _configLock.Release(); }
    }

    /// <summary>Отправка накопленных результатов пачками; при ошибке — ждём и пробуем снова, ничего не теряя.</summary>
    private async Task UploadLoopAsync(CancellationToken ct)
    {
        const int batchSize = 500;
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(5, _config.Current.ResultsFlushSeconds));
            try
            {
                var items = _queue.Peek(batchSize);
                if (items.Count > 0)
                {
                    var batch = new ResultsBatch { AgentId = _identity.AgentId, SentAt = DateTimeOffset.UtcNow };
                    foreach (var (_, r) in items) batch.Results.Add(r);

                    var ack = await _server.PostResultsAsync(batch, ct);
                    _queue.Ack(items[items.Count - 1].id);
                    _log.LogDebug("Отправлено результатов: {Sent}, принято сервером: {Accepted}", items.Count, ack.Accepted);

                    if (ack.ConfigVersion != _config.Version) await RefreshConfigAsync(ct, force: true);
                    if (items.Count == batchSize) delay = TimeSpan.FromMilliseconds(200); // разгребаем буфер быстрее
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning("Отправка результатов не удалась: {Error}. В буфере {Count}", ErrorText.Full(ex), _queue.Count());
                delay = TimeSpan.FromSeconds(30);
            }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Как запущен агент: службой (можно самообновляться) или в консоли.</summary>
public sealed class AgentRunMode
{
    public bool AsService { get; }
    public AgentRunMode(bool asService) => AsService = asService;
}

/// <summary>agent.update — проверить и установить обновление с сервера.</summary>
public sealed class AgentUpdateHandler : ICommandHandler
{
    private readonly AgentUpdater _updater;
    public AgentUpdateHandler(AgentUpdater updater) => _updater = updater;
    public string Type => CommandTypes.AgentUpdate;
    public Task<string> ExecuteAsync(CommandEnvelope cmd, CancellationToken ct) => _updater.CheckAndInstallAsync(ct);
}
