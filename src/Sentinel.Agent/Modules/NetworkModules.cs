using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Contracts.Modules;
using Sentinel.Contracts.Protocol;

namespace Sentinel.Agent.Modules;

/// <summary>ICMP-доступность хостов в сети клиента.</summary>
public sealed class PingModule : IModule
{
    public string Id => ModuleIds.NetPing;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<PingModuleSettings>();
        if (s.Targets.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного адреса") };

        var tasks = s.Targets.Where(t => !string.IsNullOrWhiteSpace(t.Address)).Select(t => PingOne(ctx, t, s, ct));
        return await Task.WhenAll(tasks);
    }

    private static async Task<CheckResult> PingOne(ModuleContext ctx, PingTarget t, PingModuleSettings s, CancellationToken ct)
    {
        var key = t.Address.Trim();
        var label = string.IsNullOrWhiteSpace(t.Name) ? key : $"{t.Name} ({key})";
        var attempts = Math.Max(1, s.Attempts);
        var latencies = new List<long>();
        string? lastError = null;

        using (var ping = new Ping())
        {
            for (var i = 0; i < attempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(key, Math.Max(200, s.TimeoutMs));
                    if (reply.Status == IPStatus.Success) latencies.Add(reply.RoundtripTime);
                    else lastError = reply.Status.ToString();
                }
                catch (Exception ex) { lastError = ex.InnerException?.Message ?? ex.Message; }
                if (i + 1 < attempts) await Task.Delay(200, ct);
            }
        }

        if (latencies.Count == 0)
            return ctx.Result(key, t.Critical ? CheckStatus.Critical : CheckStatus.Warning, $"{label}: недоступен ({lastError})", new { attempts, lost = attempts });

        var avg = latencies.Average();
        var lost = attempts - latencies.Count;
        var status = CheckStatus.Ok;
        if (lost > 0) status = CheckStatus.Warning;
        if (s.WarnLatencyMs > 0 && avg > s.WarnLatencyMs) status = CheckStatus.Warning;

        var r = ctx.Result(key, status, $"{label}: {avg:0} мс" + (lost > 0 ? $", потеряно {lost} из {attempts}" : ""), new { attempts, lost, latencies });
        r.Metrics.Add(new MetricSample { Name = "ping_ms", Value = Math.Round(avg, 1), Unit = "ms" });
        r.Metrics.Add(new MetricSample { Name = "ping_loss", Value = lost });
        return r;
    }
}

/// <summary>Доступность TCP-порта; для TLS — рукопожатие и срок действия сертификата.</summary>
public sealed class TcpModule : IModule
{
    public string Id => ModuleIds.NetTcp;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(ModuleContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings<TcpModuleSettings>();
        if (s.Targets.Count == 0) return new[] { ctx.Result("", CheckStatus.Unknown, "Не задано ни одного порта") };
        var tasks = s.Targets.Where(t => !string.IsNullOrWhiteSpace(t.Host) && t.Port > 0).Select(t => CheckOne(ctx, t, s, ct));
        return await Task.WhenAll(tasks);
    }

    private static async Task<CheckResult> CheckOne(ModuleContext ctx, TcpTarget t, TcpModuleSettings s, CancellationToken ct)
    {
        var key = $"{t.Host.Trim()}:{t.Port}";
        var label = string.IsNullOrWhiteSpace(t.Name) ? key : $"{t.Name} ({key})";
        var fail = t.Critical ? CheckStatus.Critical : CheckStatus.Warning;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(t.Host.Trim(), t.Port);
                var done = await Task.WhenAny(connect, Task.Delay(Math.Max(500, s.TimeoutMs), ct));
                if (done != connect) return ctx.Result(key, fail, $"{label}: таймаут подключения ({s.TimeoutMs} мс)");
                await connect; // пробросит исключение

                if (!t.Tls)
                {
                    var r = ctx.Result(key, CheckStatus.Ok, $"{label}: порт открыт, {sw.ElapsedMilliseconds} мс");
                    r.Metrics.Add(new MetricSample { Name = "tcp_connect_ms", Value = sw.ElapsedMilliseconds, Unit = "ms" });
                    return r;
                }

                X509Certificate2? cert = null;
                var chainOk = true;
                using (var ssl = new SslStream(client.GetStream(), false, (_, c, _, errors) => { chainOk = errors == SslPolicyErrors.None; return true; }))
                {
                    await ssl.AuthenticateAsClientAsync(t.Host.Trim());
                    if (ssl.RemoteCertificate is not null) cert = new X509Certificate2(ssl.RemoteCertificate);
                }

                if (cert is null) return ctx.Result(key, CheckStatus.Warning, $"{label}: TLS без сертификата");
                var daysLeft = (cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
                var details = new { subject = cert.Subject, issuer = cert.Issuer, notAfter = cert.NotAfter.ToUniversalTime(), daysLeft = Math.Round(daysLeft), chainValid = chainOk };
                CheckStatus status; string msg;
                if (daysLeft <= 0) { status = fail; msg = $"{label}: сертификат истёк {cert.NotAfter:dd.MM.yyyy}"; }
                else if (daysLeft <= t.CertWarnDays) { status = CheckStatus.Warning; msg = $"{label}: сертификат истекает через {daysLeft:0} дн."; }
                else if (!chainOk) { status = CheckStatus.Warning; msg = $"{label}: сертификат не доверенный, действует ещё {daysLeft:0} дн."; }
                else { status = CheckStatus.Ok; msg = $"{label}: TLS ок, сертификат ещё {daysLeft:0} дн."; }

                var res = ctx.Result(key, status, msg, details);
                res.Metrics.Add(new MetricSample { Name = "cert_days_left", Value = Math.Round(daysLeft, 1), Unit = "d" });
                res.Metrics.Add(new MetricSample { Name = "tcp_connect_ms", Value = sw.ElapsedMilliseconds, Unit = "ms" });
                return res;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ctx.Result(key, fail, $"{label}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
