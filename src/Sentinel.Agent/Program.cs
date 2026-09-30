using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Sentinel.Agent.Infrastructure;
using Sentinel.Agent.Modules;
using Sentinel.Agent.Services;
using Sentinel.Contracts.Protocol;
using Serilog;

namespace Sentinel.Agent;

public static class Program
{
    public static int Main(string[] args)
    {
        AgentPaths.EnsureDirectories();
        // UTF-8 в консоли корректно показывают только Windows 10+/2016+; на 2008 R2 оставляем OEM-кодировку (cp866 — кириллица есть).
        if (Environment.OSVersion.Version.Major >= 10) { try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { } }
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(System.IO.Path.Combine(AgentPaths.LogDir, "agent-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : (Environment.UserInteractive ? "help" : "service");
            switch (command)
            {
                case "enroll": return Enroll(ParseOptions(args.Skip(1))).GetAwaiter().GetResult();
                case "install": return RequireAdmin() ?? Install(ParseOptions(args.Skip(1)));
                case "uninstall": return RequireAdmin() ?? Uninstall();
                case "run": RunHost(args, asService: false); return 0;
                case "service": RunHost(args, asService: true); return 0;
                case "status": return Status();
                case "diag": return Diag().GetAwaiter().GetResult();
                case "proxy": return SetProxy(args.Skip(1).FirstOrDefault());
                case "allow": return Allow(args.Skip(1).ToArray());
                case "version": Console.WriteLine(AgentPaths.Version); return 0;
                default: PrintHelp(); return command == "help" ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Агент завершился с ошибкой");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void RunHost(string[] args, bool asService)
    {
        var builder = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IModule, SystemModule>();
                services.AddSingleton<IModule, ServicesModule>();
                services.AddSingleton<IModule, DisksModule>();
                services.AddSingleton<IModule, EventLogModule>();
                services.AddSingleton<IModule, PingModule>();
                services.AddSingleton<IModule, TcpModule>();
                services.AddSingleton<IModule, HyperVModule>();
                services.AddSingleton<IModule, Backup.FilesBackupModule>();
                services.AddSingleton<IModule, Backup.OneCFileBackupModule>();
                services.AddSingleton<IModule, Backup.MsSqlBackupModule>();
                services.AddSingleton<IModule, Backup.PostgresBackupModule>();
                services.AddSingleton<IModule, Cctv.HikvisionModule>();
                services.AddSingleton<IModule, Cctv.DahuaModule>();
                services.AddSingleton<IModule, Cctv.OnvifModule>();
                services.AddSingleton<IModule, Cctv.RtspModule>();
                services.AddSingleton<ModuleRegistry>();
                services.AddSingleton(new AgentRunMode(asService));
                services.AddHostedService<AgentWorker>();
            });
        if (asService) builder.UseWindowsService(o => o.ServiceName = AgentPaths.ServiceName);
        builder.Build().Run();
    }

    // ---------------- enroll ----------------

    private static async Task<int> Enroll(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("server", out var server) || !o.TryGetValue("token", out var token))
        {
            Console.Error.WriteLine("Использование: SentinelAgent.exe enroll --server https://monitor.example.com --token <token> [--allow ping,config.refresh,service.*,backup.*,reboot] [--pin <base64>]");
            return 2;
        }

        var identity = new AgentIdentity { ServerUrl = server.TrimEnd('/') };
        if (o.TryGetValue("allow", out var allow))
            identity.AllowedCommands = allow.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (o.TryGetValue("pin", out var pin)) identity.CertificatePin = pin;
        if (o.TryGetValue("proxy", out var proxySetting)) identity.Proxy = proxySetting;

        var mel = Log.ForContext<ServerClient>().AsMel();
        if (identity.CertificatePin is not null) await ServerClient.FetchCaCertificateAsync(identity, mel, CancellationToken.None);
        using (var client = new ServerClient(identity.ServerUrl, identity, mel))
        {
            Console.WriteLine($"Регистрация на {identity.ServerUrl} ...");
            var resp = await client.EnrollAsync(new EnrollRequest
            {
                Token = token,
                Hostname = Environment.MachineName,
                OsVersion = OsDescription(),
                AgentVersion = AgentPaths.Version,
                MachineId = MachineGuid(),
                Is64Bit = Environment.Is64BitOperatingSystem,
            }, CancellationToken.None);

            identity.AgentId = resp.AgentId;
            identity.HostId = resp.HostId;
            identity.ServerPublicKey = resp.ServerPublicKey;
            identity.SetSecret(Convert.FromBase64String(resp.Secret));
            // Pin от сервера применяется, только если инженер не задал свой явно.
            if (identity.CertificatePin is null && !string.IsNullOrWhiteSpace(resp.CertificatePin)) identity.CertificatePin = resp.CertificatePin;
            identity.EnrolledAt = DateTimeOffset.UtcNow;
            identity.Save();
            // Новая регистрация = новый хост (возможно, на другом сервере): кэш конфигурации и офлайн-буфер от старого не нужны.
            foreach (var stale in new[] { AgentPaths.ConfigCacheFile, AgentPaths.QueueDb })
                try { if (System.IO.File.Exists(stale)) System.IO.File.Delete(stale); } catch (Exception ex) { Console.WriteLine($"Не удалось удалить {stale}: {ex.Message}"); }
        }

        Console.WriteLine($"Готово. AgentId = {identity.AgentId}");
        Console.WriteLine($"Разрешённые команды: {string.Join(", ", identity.AllowedCommands)}");
        Console.WriteLine($"Файл: {AgentPaths.IdentityFile}");
        if (IsServiceInstalled())
        {
            Console.WriteLine("Служба установлена — перезапускаю.");
            Sc("stop"); Thread.Sleep(2000); Sc("start");
        }
        else Console.WriteLine("Теперь установите службу: SentinelAgent.exe install");
        return 0;
    }

    // ---------------- install / uninstall ----------------

    private static int Install(Dictionary<string, string> o)
    {
        var exe = AgentPaths.ExePath;
        if (IsServiceInstalled())
        {
            Console.WriteLine("Служба уже установлена.");
            return 0;
        }
        var rc = Sc($"create {AgentPaths.ServiceName} binPath= \"\\\"{exe}\\\" service\" start= auto DisplayName= \"{AgentPaths.ServiceDisplayName}\"");
        if (rc != 0) return rc;
        Sc($"description {AgentPaths.ServiceName} \"Мониторинг инфраструктуры Sentinel: проверки, бэкапы, удалённые действия\"");
        Sc($"failure {AgentPaths.ServiceName} reset= 86400 actions= restart/10000/restart/30000/restart/60000");
        EnableTls12InRegistry();
        Console.WriteLine("Служба установлена.");
        if (!o.ContainsKey("no-start")) { Sc("start"); Console.WriteLine("Служба запущена."); }
        return 0;
    }

    private static int Uninstall()
    {
        if (!IsServiceInstalled()) { Console.WriteLine("Служба не установлена."); return 0; }
        Sc("stop");
        Thread.Sleep(2000);
        var rc = Sc($"delete {AgentPaths.ServiceName}");
        if (rc == 0) Console.WriteLine("Служба удалена. Файлы в " + AgentPaths.Root + " сохранены.");
        return rc;
    }

    private static int Status()
    {
        var id = AgentIdentity.Load();
        Console.WriteLine($"Версия:        {AgentPaths.Version}");
        Console.WriteLine($"Служба:        {(IsServiceInstalled() ? "установлена" : "не установлена")}");
        Console.WriteLine($"Регистрация:   {(id?.IsEnrolled == true ? $"{id.AgentId} на {id.ServerUrl} ({id.EnrolledAt:g})" : "нет")}");
        if (id is not null) Console.WriteLine($"Команды:       {string.Join(", ", id.AllowedCommands)}");
        Console.WriteLine($"Каталог:       {AgentPaths.Root}");
        return 0;
    }

    /// <summary>
    /// Самодиагностика связи с сервером: DNS, TCP 443, прокси, HTTPS, подписанный запрос — с полным текстом ошибок.
    /// Запускается от текущего пользователя: если здесь всё «ок», а служба не на связи — разница в учётке LocalSystem (обычно прокси).
    /// </summary>
    private static async Task<int> Diag()
    {
        var id = AgentIdentity.Load();
        if (id is null || !id.IsEnrolled) { Console.WriteLine("Агент не зарегистрирован."); return 1; }
        var uri = new Uri(id.ServerUrl);
        var failed = 0;
        void Line(bool ok, string what, string detail) { if (!ok) failed++; Console.WriteLine($"[{(ok ? " ок " : "СБОЙ")}] {what}: {detail}"); }

        Console.WriteLine($"Агент {AgentPaths.Version}, сервер {id.ServerUrl}, пользователь {Environment.UserDomainName}\\{Environment.UserName}, время UTC {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var ips = await System.Net.Dns.GetHostAddressesAsync(uri.Host);
            Line(ips.Length > 0, "DNS", string.Join(", ", ips.Select(i => i.ToString())));
        }
        catch (Exception ex) { Line(false, "DNS", ErrorText.Full(ex)); }

        try
        {
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                var connect = tcp.ConnectAsync(uri.Host, uri.Port);
                var ok = await Task.WhenAny(connect, Task.Delay(5000)) == connect && tcp.Connected;
                Line(ok, $"TCP {uri.Port}", ok ? "соединение установлено" : "нет ответа за 5 с — сеть, брандмауэр или NAT loopback (нужна строка в hosts)");
            }
        }
        catch (Exception ex) { Line(false, $"TCP {uri.Port}", ErrorText.Full(ex)); }

        Console.WriteLine($"[info] Прокси: {ServerClient.DescribeProxy(id, uri)}. Сменить: SentinelAgent.exe proxy none|system|http://адрес:порт");

        var client = new ServerClient(id.ServerUrl, id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        try
        {
            using (var http = new System.Net.Http.HttpClient(ServerClient.CreateHandler(id, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)) { Timeout = TimeSpan.FromSeconds(15) })
            {
                var r = await http.GetAsync(new Uri(uri, "/api/health"));
                Line(r.IsSuccessStatusCode, "HTTPS /api/health", $"HTTP {(int)r.StatusCode}");
            }
        }
        catch (Exception ex) { Line(false, "HTTPS /api/health", ErrorText.Full(ex)); }

        try
        {
            var cfg = await client.GetConfigAsync(CancellationToken.None);
            Line(true, "Подписанный запрос (конфигурация)", $"v{cfg.Version}, проверок {cfg.Checks.Count}");
        }
        catch (Exception ex) { Line(false, "Подписанный запрос (конфигурация)", ErrorText.Full(ex)); }

        Console.WriteLine(failed == 0
            ? "Всё в порядке от этого пользователя. Если служба всё равно не на связи — сравните с логом службы: у LocalSystem нет прокси вашей учётки."
            : $"Проблем: {failed}. Лог службы: {AgentPaths.LogDir}");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>
    /// Белый список команд — единственное место, где его можно поменять (сервер этого не умеет намеренно).
    /// allow — показать; allow add a,b — добавить; allow remove a,b — убрать. Изменение — только от администратора.
    /// </summary>
    private static int Allow(string[] args)
    {
        var id = AgentIdentity.Load();
        if (id is null || !id.IsEnrolled) { Console.WriteLine("Агент не зарегистрирован."); return 1; }
        if (args.Length == 0)
        {
            Console.WriteLine("Разрешённые команды: " + string.Join(", ", id.AllowedCommands));
            Console.WriteLine("Изменить: SentinelAgent.exe allow add agent.update   |   SentinelAgent.exe allow remove exec");
            return 0;
        }
        if (RequireAdmin() is { } rc) return rc;

        var op = args[0].ToLowerInvariant();
        var items = string.Join(",", args.Skip(1)).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if ((op != "add" && op != "remove") || items.Count == 0)
        {
            Console.Error.WriteLine("Использование: SentinelAgent.exe allow add|remove команда[,команда]");
            return 2;
        }
        foreach (var item in items)
        {
            if (op == "add" && !id.AllowedCommands.Contains(item, StringComparer.OrdinalIgnoreCase)) id.AllowedCommands.Add(item);
            if (op == "remove") id.AllowedCommands.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        }
        id.Save();
        Console.WriteLine("Разрешённые команды: " + string.Join(", ", id.AllowedCommands));
        if (IsServiceInstalled()) { Console.WriteLine("Перезапускаю службу."); Sc("stop"); Thread.Sleep(2000); Sc("start"); }
        return 0;
    }

    /// <summary>
    /// Прокси для связи с сервером: без аргумента — показать; none — напрямую; system — системные настройки; иначе адрес.
    /// Сохраняется в agent.json, служба перезапускается.
    /// </summary>
    private static int SetProxy(string? value)
    {
        var id = AgentIdentity.Load();
        if (id is null || !id.IsEnrolled) { Console.WriteLine("Агент не зарегистрирован."); return 1; }
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine($"Прокси: {(string.IsNullOrWhiteSpace(id.Proxy) ? "system" : id.Proxy)} → {ServerClient.DescribeProxy(id, new Uri(id.ServerUrl))}");
            return 0;
        }
        id.Proxy = value!.Trim().Equals("system", StringComparison.OrdinalIgnoreCase) ? null : value.Trim();
        try { id.Save(); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Нет прав на запись настроек — запустите от имени администратора."); return 1; }
        Console.WriteLine($"Прокси сохранён: {(id.Proxy ?? "system")} → {ServerClient.DescribeProxy(id, new Uri(id.ServerUrl))}");
        if (IsServiceInstalled())
        {
            if (IsAdmin()) { Console.WriteLine("Перезапускаю службу."); Sc("stop"); Thread.Sleep(2000); Sc("start"); }
            else Console.WriteLine("Перезапустите службу SentinelAgent (от администратора), чтобы настройка вступила в силу.");
        }
        return 0;
    }

    /// <summary>Server 2008 R2 / Win7: TLS 1.2 для клиента выключен по умолчанию — включаем (после установки KB3140245 работает).</summary>
    private static void EnableTls12InRegistry()
    {
        try
        {
            using (var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client"))
            {
                key?.SetValue("Enabled", 1, RegistryValueKind.DWord);
                key?.SetValue("DisabledByDefault", 0, RegistryValueKind.DWord);
            }
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = hklm.CreateSubKey(@"SOFTWARE\Microsoft\.NETFramework\v4.0.30319"))
                {
                    key?.SetValue("SchUseStrongCrypto", 1, RegistryValueKind.DWord);
                    key?.SetValue("SystemDefaultTlsVersions", 1, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("Не удалось включить TLS 1.2 в реестре: " + ex.Message); }
    }

    // ---------------- утилиты ----------------

    /// <summary>Управление службой требует прав администратора; манифест не форсирует UAC, чтобы run/status работали без него.</summary>
    private static bool IsAdmin()
    {
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static int? RequireAdmin()
    {
        if (IsAdmin()) return null;
        Console.Error.WriteLine("Требуются права администратора: запустите консоль от имени администратора.");
        return 5;
    }

    private static bool IsServiceInstalled()
        => System.ServiceProcess.ServiceController.GetServices().Any(s => s.ServiceName.Equals(AgentPaths.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static int Sc(string arguments)
    {
        if (arguments == "start" || arguments == "stop") arguments = $"{arguments} {AgentPaths.ServiceName}";
        var p = Process.Start(new ProcessStartInfo("sc.exe", arguments) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && p.ExitCode != 1062 /* not started */ && p.ExitCode != 1056 /* already running */)
            Console.Error.WriteLine(output.Trim());
        return p.ExitCode;
    }

    private static string MachineGuid()
    {
        try
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch { return ""; }
    }

    private static string OsDescription()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                var name = key?.GetValue("ProductName") as string;
                var build = key?.GetValue("CurrentBuild") as string;
                if (!string.IsNullOrEmpty(name)) return $"{name} (build {build})";
            }
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                if (key is not null) dict[key] = "true";
                key = a.Substring(2);
            }
            else if (key is not null) { dict[key] = a; key = null; }
        }
        if (key is not null) dict[key] = "true";
        return dict;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"Sentinel Agent v{AgentPaths.Version}");
        Console.WriteLine();
        Console.WriteLine("  enroll --server <url> --token <token> [--allow <список>] [--pin <base64>]   регистрация на сервере");
        Console.WriteLine("  install [--no-start]      установить службу Windows");
        Console.WriteLine("  uninstall                 удалить службу");
        Console.WriteLine("  run                       запустить в консоли (отладка)");
        Console.WriteLine("  status                    показать состояние");
        Console.WriteLine("  diag                      проверить связь с сервером (DNS, TCP, прокси, HTTPS, подпись)");
        Console.WriteLine("  proxy [none|system|url]   прокси для связи с сервером (без аргумента — показать)");
        Console.WriteLine("  allow [add|remove X,Y]    белый список команд (без аргумента — показать)");
        Console.WriteLine();
        Console.WriteLine("  --allow: типы команд, разрешённые этому агенту. По умолчанию: ping,config.refresh,agent.update,service.*,backup.*,cctv.*");
        Console.WriteLine("           Например: --allow ping,config.refresh,service.*,reboot,exec");
    }

    /// <summary>Serilog ILogger → Microsoft ILogger для классов, не проходящих через DI (enroll).</summary>
    private static Microsoft.Extensions.Logging.ILogger AsMel(this Serilog.ILogger logger)
        => new Serilog.Extensions.Logging.SerilogLoggerFactory(logger).CreateLogger("Sentinel.Agent");
}
