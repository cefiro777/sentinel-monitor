using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentinel.Agent.Commands;
using Sentinel.Agent.Infrastructure;
using Sentinel.Contracts.Json;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Services;

/// <summary>Постоянный канал к серверу: хартбиты, приём команд, уведомления о смене конфига.</summary>
public sealed class HubClient : IAsyncDisposable
{
    private readonly ServerClient _server;
    private readonly AgentIdentity _identity;
    private readonly CommandDispatcher _dispatcher;
    private readonly Func<int, CancellationToken, Task> _onConfigChanged;
    private readonly Func<int> _queueDepth;
    private readonly ConfigStore _config;
    private readonly ILogger<HubClient> _log;
    private readonly HubConnection _hub;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    public HubClient(ServerClient server, AgentIdentity identity, CommandDispatcher dispatcher, ConfigStore config,
        Func<int, CancellationToken, Task> onConfigChanged, Func<int> queueDepth, ILogger<HubClient> log)
    {
        _server = server;
        _identity = identity;
        _dispatcher = dispatcher;
        _config = config;
        _onConfigChanged = onConfigChanged;
        _queueDepth = queueDepth;
        _log = log;

        var url = identity.ServerUrl.TrimEnd('/') + AgentHubContract.Path;
        _hub = new HubConnectionBuilder()
            .WithUrl(url, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(_server.CreateHubAccessToken());
                // На Server 2008 R2 нет ClientWebSocket — SignalR сам откатится на SSE/long polling.
                o.CloseTimeout = TimeSpan.FromSeconds(10);
                // Прокси и pin — те же, что у остальных запросов агента.
                o.HttpMessageHandlerFactory = _ => ServerClient.CreateHandler(identity, _log);
                var proxy = ServerClient.ResolveProxy(identity);
                if (proxy != ServerClient.SystemProxy) o.WebSocketConfiguration = ws => ws.Proxy = proxy ?? new System.Net.WebProxy();
            })
            .WithAutomaticReconnect(new BackoffRetryPolicy())
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = SentinelJson.Options.PropertyNamingPolicy;
                foreach (var c in SentinelJson.Options.Converters) o.PayloadSerializerOptions.Converters.Add(c);
            })
            .Build();

        _hub.On<CommandEnvelope>(AgentHubContract.ExecuteCommand, async cmd =>
        {
            var result = await _dispatcher.DispatchAsync(cmd, CancellationToken.None);
            try { await _hub.InvokeAsync(AgentHubContract.CommandResult, result); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось отправить результат команды {Id}", cmd.Id); }
        });

        _hub.On<int>(AgentHubContract.ConfigChanged, async version =>
        {
            _log.LogInformation("Сервер сообщил о новой конфигурации v{Version}", version);
            try { await _onConfigChanged(version, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "Не удалось обновить конфигурацию"); }
        });

        _hub.Reconnecting += ex => { _log.LogWarning("Связь с сервером потеряна, переподключение: {Reason}", ex?.Message); return Task.CompletedTask; };
        _hub.Reconnected += id => { _log.LogInformation("Связь с сервером восстановлена"); return Task.CompletedTask; };
        _hub.Closed += ex => { _log.LogWarning("Канал закрыт: {Reason}", ex?.Message ?? "штатно"); return Task.CompletedTask; };
    }

    /// <summary>Держит соединение и шлёт хартбиты до отмены.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_hub.State == HubConnectionState.Disconnected)
            {
                try
                {
                    await _hub.StartAsync(ct);
                    _log.LogInformation("Подключено к серверу ({Url})", _identity.ServerUrl);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _log.LogWarning("Не удалось подключиться к серверу: {Error}. Повтор через 30 с", ErrorText.Full(ex));
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
                    continue;
                }
            }

            if (_hub.State == HubConnectionState.Connected)
            {
                try
                {
                    await _hub.InvokeAsync(AgentHubContract.Heartbeat, new HeartbeatMessage
                    {
                        At = DateTimeOffset.UtcNow,
                        AgentVersion = AgentPaths.Version,
                        ConfigVersion = _config.Version,
                        QueueDepth = _queueDepth(),
                        UptimeSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogDebug("Хартбит не отправлен: {Error}", ex.Message); }
            }

            var period = Math.Max(10, _config.Current.HeartbeatSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(period), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _hub.StopAsync(); } catch { }
        await _hub.DisposeAsync();
    }

    /// <summary>1, 2, 5, 10, 30, 60 секунд, затем каждую минуту — без ограничения по числу попыток.</summary>
    private sealed class BackoffRetryPolicy : IRetryPolicy
    {
        private static readonly int[] Steps = { 1, 2, 5, 10, 30, 60 };
        public TimeSpan? NextRetryDelay(RetryContext ctx)
            => TimeSpan.FromSeconds(Steps[Math.Min(ctx.PreviousRetryCount, Steps.Length - 1)]);
    }
}
