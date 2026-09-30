using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>Состояние выбранных служб Windows с опциональным автозапуском.</summary>
public sealed class ServicesModule : IModule
{
    private readonly ILogger<ServicesModule> _log;
    public ServicesModule(ILogger<ServicesModule> log) => _log = log;

    public string Id => ModuleIds.Services;

    public Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<ServicesModuleSettings>();
        var results = new List<CheckResult>();
        if (s.Services.Count == 0)
        {
            results.Add(ctx.Result("", CheckStatus.Unknown, "Не выбрано ни одной службы"));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var startModes = ReadStartModes();

        foreach (var target in s.Services)
        {
            ct.ThrowIfCancellationRequested();
            var name = target.Name.Trim();
            if (name.Length == 0) continue;

            try
            {
                using (var sc = new ServiceController(name))
                {
                    var display = sc.DisplayName; // бросит InvalidOperationException, если службы нет
                    var status = sc.Status;
                    startModes.TryGetValue(name, out var startMode);
                    var restarted = false;

                    if (status != ServiceControllerStatus.Running && target.AutoRestart &&
                        (status == ServiceControllerStatus.Stopped || status == ServiceControllerStatus.Paused))
                    {
                        try
                        {
                            if (status == ServiceControllerStatus.Paused) sc.Continue(); else sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                            sc.Refresh();
                            status = sc.Status;
                            restarted = status == ServiceControllerStatus.Running;
                            _log.LogWarning("Служба {Service} была {Was}, автозапуск: {Result}", name, status, restarted ? "успешно" : "не удалось");
                        }
                        catch (Exception ex) { _log.LogError(ex, "Автозапуск службы {Service} не удался", name); }
                    }

                    var details = new { displayName = display, status = status.ToString(), startMode, autoRestarted = restarted };
                    if (status == ServiceControllerStatus.Running)
                        results.Add(ctx.Result(name, CheckStatus.Ok, restarted ? $"{display}: запущена автоматически после остановки" : $"{display}: работает", details));
                    else if (status == ServiceControllerStatus.StartPending || status == ServiceControllerStatus.ContinuePending)
                        results.Add(ctx.Result(name, CheckStatus.Warning, $"{display}: запускается", details));
                    else
                        results.Add(ctx.Result(name, target.Critical ? CheckStatus.Critical : CheckStatus.Warning, $"{display}: {Translate(status)}", details));
                }
            }
            catch (InvalidOperationException)
            {
                results.Add(ctx.Result(name, CheckStatus.Unknown, $"Служба «{name}» не найдена на этом сервере"));
            }
            catch (Exception ex)
            {
                results.Add(ctx.Result(name, CheckStatus.Unknown, $"{name}: {ex.Message}"));
            }
        }
        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    private static Dictionary<string, string> ReadStartModes()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, StartMode FROM Win32_Service"))
            foreach (ManagementObject o in searcher.Get())
                map[(string)o["Name"]] = (string)o["StartMode"];
        }
        catch { /* WMI недоступен — обойдёмся без режима запуска */ }
        return map;
    }

    private static string Translate(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.Stopped => "остановлена",
        ServiceControllerStatus.Paused => "приостановлена",
        ServiceControllerStatus.StopPending => "останавливается",
        ServiceControllerStatus.PausePending => "приостанавливается",
        _ => s.ToString(),
    };
}
