using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sentinel.Contracts.Json;
using Sentinel.Server.Core.Abstractions;
using Sentinel.Server.Core.Entities;
using Sentinel.Server.Data;
using Sentinel.Server.Endpoints;
using Sentinel.Server.Hubs;
using Sentinel.Server.Options;
using Sentinel.Server.Security;
using Sentinel.Server.Services;
using Serilog;

// Служба Windows стартует с текущим каталогом System32 — относительные пути (data, wwwroot) считаем от exe.
var asWindowsService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
if (asWindowsService) Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = asWindowsService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "SentinelServer");

var services = builder.Services;
var config = builder.Configuration;
// Настройки вне каталога программы (установка на Windows: %ProgramData%\Sentinel\Server\appsettings.json):
// переживают обновление и удаление вместе с данными. Sentinel.Server.exe --config <путь>
if (config["config"] is { Length: > 0 } extraConfig) config.AddJsonFile(Path.GetFullPath(extraConfig), optional: false, reloadOnChange: false);
var sentinel =config.GetSection(SentinelOptions.Section).Get<SentinelOptions>() ?? new SentinelOptions();
var dataDirFull = Path.GetFullPath(sentinel.DataDir);

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext:l}: {Message:lj}{NewLine}{Exception}");
    // У службы нет консоли — без файла лог некуда смотреть.
    if (sentinel.LogToFile || asWindowsService)
        cfg.WriteTo.File(Path.Combine(dataDirFull, "logs", "server-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext:l}: {Message:lj}{NewLine}{Exception}");
});

// Собственный HTTPS (Sentinel:Tls) — для установки без Caddy/nginx; адреса и порты — из Urls.
var ownCert = ServerTls.Load(sentinel.Tls, dataDirFull, sentinel.PublicUrl);
if (ownCert is not null) builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = ownCert));

services.Configure<SentinelOptions>(config.GetSection(SentinelOptions.Section));
services.AddSingleton(new ServerCertificate(ownCert, ServerTls.PinFor(sentinel.Tls, ownCert)));
services.AddSingleton<CertificatePinProvider>();
services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirFull, "dataprotection")));

// --- БД ---
var connectionString = config.GetConnectionString("Default")
    ?? Environment.GetEnvironmentVariable("SENTINEL_DB")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default / SENTINEL_DB.");
services.AddDbContext<SentinelDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

// --- Криптография и безопасность ---
services.AddSingleton<KeyStore>();
services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
services.AddSingleton<ICommandSigner, Ed25519CommandSigner>();
services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
services.AddSingleton<JwtTokenService>();
services.AddMemoryCache();

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = config.GetSection(SentinelOptions.Section).Get<SentinelOptions>()?.Jwt ?? new JwtOptions();
        o.MapInboundClaims = false; // оставляем "sub", "role" как есть, без переименования в схемы Microsoft
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            ValidateIssuerSigningKey = true,
        };
        // Ключ известен только после построения KeyStore, поэтому берём его лениво.
        o.Events = new JwtBearerEvents
        {
            // Промежуточный TOTP-токен годится только для /auth/totp/verify, а не как сессия.
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal?.FindFirst(JwtTokenService.ClaimPurpose) is not null) ctx.Fail("challenge token");
                return Task.CompletedTask;
            },
            OnMessageReceived = ctx =>
            {
                // Пути агентов обслуживает схема Agent — JWT туда не лезет и не шумит в логах.
                if (ctx.Request.Path.StartsWithSegments("/hubs/agent") || ctx.Request.Path.StartsWithSegments("/api/agent"))
                {
                    ctx.NoResult();
                    return Task.CompletedTask;
                }
                ctx.Options.TokenValidationParameters.IssuerSigningKey =
                    ctx.HttpContext.RequestServices.GetRequiredService<JwtTokenService>().SecurityKey;
                return Task.CompletedTask;
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthenticationHandler.Scheme, null);

services.AddAuthorizationBuilder()
    .AddPolicy(AgentEndpoints.AgentPolicy, p => p.AddAuthenticationSchemes(AgentAuthenticationHandler.Scheme).RequireAuthenticatedUser())
    .AddPolicy(AuthEndpoints.UserPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser())
    .AddPolicy(AuthEndpoints.OperatorPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim(JwtTokenService.ClaimRole, nameof(UserRole.Admin), nameof(UserRole.Engineer)))
    .AddPolicy(AuthEndpoints.AdminPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim(JwtTokenService.ClaimRole, nameof(UserRole.Admin)))
    // Удалённые действия на серверах клиентов — только с подтверждённым вторым фактором.
    .AddPolicy(AuthEndpoints.MfaOperatorPolicy, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim(JwtTokenService.ClaimRole, nameof(UserRole.Admin), nameof(UserRole.Engineer))
        .RequireClaim(JwtTokenService.ClaimMfa, "true"));

// --- Приложение ---
services.AddScoped<AgentConfigBuilder>();
services.AddScoped<ResultIngestService>();
services.AddScoped<IncidentService>();
services.AddScoped<BackupIngest>();
services.AddSingleton<SettingsProtector>();
services.AddSingleton<ServicePresets>();
services.AddSingleton<EventHints>();
services.AddSingleton<AgentPackageService>();
services.AddAntiforgery();
services.AddHostedService<BackupOverdueWatcher>();
services.AddScoped<CommandService>();
services.AddSingleton<AgentConnectionTracker>();
services.AddSingleton<IAgentMessenger, SignalRAgentMessenger>();
services.AddHostedService<AgentOfflineWatcher>();
services.AddHostedService<CommandExpiryWorker>();
services.AddHostedService<IncidentReminderWorker>();
services.AddHostedService<RetentionWorker>();

// --- Оповещения ---
services.AddHttpClient("telegram", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var proxy = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SentinelOptions>>().Value.TelegramProxy;
    var handler = new SocketsHttpHandler();
    if (!string.IsNullOrWhiteSpace(proxy))
    {
        // socks5://user:pass@host:port — WebProxy сам логин/пароль из URL не берёт. socks5h (DNS на стороне прокси) —
        // то же, что socks5 в .NET (имя хоста всегда уходит прокси), но такую схему SocketsHttpHandler не принимает.
        var uri = new Uri(System.Text.RegularExpressions.Regex.Replace(proxy.Trim(), "^socks5h://", "socks5://", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var wp = new System.Net.WebProxy(new UriBuilder(uri) { UserName = "", Password = "" }.Uri);
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
            wp.Credentials = new System.Net.NetworkCredential(parts[0], parts.Length > 1 ? parts[1] : "");
        }
        handler.Proxy = wp; handler.UseProxy = true;
    }
    return handler;
});
services.AddSingleton<Sentinel.Notifications.INotificationSender, Sentinel.Notifications.EmailSender>();
services.AddSingleton<Sentinel.Notifications.INotificationSender, Sentinel.Notifications.TelegramSender>();
services.AddHttpClient("max", c => c.Timeout = TimeSpan.FromSeconds(20));
services.AddSingleton<Sentinel.Notifications.INotificationSender, Sentinel.Notifications.MaxSender>();
services.AddHostedService<NotificationDispatcher>();

services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = SentinelJson.Options.PropertyNamingPolicy;
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = SentinelJson.Options.PropertyNamingPolicy;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
services.AddOpenApi();
services.AddProblemDetails();

// За nginx/Caddy: реальный IP клиента и схема https берутся из X-Forwarded-*.
services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
app.UseForwardedHeaders();

app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
    : ctx.Request.Path.StartsWithSegments("/api/agent") || ctx.Request.Path.StartsWithSegments("/hubs") ? Serilog.Events.LogEventLevel.Debug
    : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapOpenApi();
app.MapAgentEndpoints();
app.MapAuthEndpoints();
app.MapInventoryEndpoints();
app.MapIncidentEndpoints();
app.MapNotificationEndpoints();
app.MapBackupEndpoints();
app.MapCctvEndpoints();
app.MapMaintenanceEndpoints();
app.MapTemplateEndpoints();
app.MapAgentPackageEndpoints();
app.MapHub<AgentHub>(Sentinel.Contracts.Protocol.AgentHubContract.Path);
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow })).AllowAnonymous();

// SPA дашборда: собранный Sentinel.Web кладётся в wwwroot; все не-API пути отдают index.html.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await StartupTasks.RunAsync(app.Services);
app.Run();
