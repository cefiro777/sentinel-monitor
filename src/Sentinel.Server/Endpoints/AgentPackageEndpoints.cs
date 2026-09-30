using Microsoft.Extensions.Options;
using Sentinel.Server.Options;
using Sentinel.Server.Services;
using static Sentinel.Server.Endpoints.AuthEndpoints;

namespace Sentinel.Server.Endpoints;

/// <summary>Раздача дистрибутива агента и скрипта установки; загрузка нового пакета админом.</summary>
public static class AgentPackageEndpoints
{
    public static IEndpointRouteBuilder MapAgentPackageEndpoints(this IEndpointRouteBuilder app)
    {
        // Публично: zip не секрет, доверие обеспечивает подпись в манифесте и токен регистрации.
        app.MapGet("/agent/manifest.json", (AgentPackageService pkg) => pkg.GetManifest() is { } m ? Results.Ok(m) : Results.NotFound(new { error = "Пакет агента ещё не опубликован." })).AllowAnonymous();
        app.MapGet("/agent/" + AgentPackageService.ZipName, (AgentPackageService pkg) =>
            File.Exists(pkg.ZipPath) ? Results.File(pkg.ZipPath, "application/zip", AgentPackageService.ZipName) : Results.NotFound()).AllowAnonymous();
        // Корневой сертификат внутреннего CA (режим без домена). Агент сверяет его SPKI с pin из команды установки.
        app.MapGet("/agent/ca.crt", (IOptions<SentinelOptions> o, ServerCertificate own) =>
            own.Pin is not null && own.Certificate is not null
                ? Results.Text(Security.ServerTls.ToPem(own.Certificate), "application/x-pem-file")
                : File.Exists(o.Value.CaddyRootCertPath) ? Results.File(o.Value.CaddyRootCertPath, "application/x-pem-file", "ca.crt") : Results.NotFound()).AllowAnonymous();
        // Обязательно с BOM: без него PowerShell читает файл как ANSI, и русские строки в скрипте ломают синтаксис.
        app.MapGet("/agent/install.ps1", (IOptions<SentinelOptions> o) =>
        {
            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var text = InstallScript(o.Value.PublicUrl).Replace("\r\n", "\n").Replace("\n", "\r\n");
            return Results.Bytes(utf8.GetPreamble().Concat(utf8.GetBytes(text)).ToArray(), "text/plain; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost("/api/agent-package", Upload).WithTags("Agent").RequireAuthorization(AdminPolicy).DisableAntiforgery();
        app.MapGet("/api/agent-package", (AgentPackageService pkg) => Results.Ok(pkg.GetManifest())).WithTags("Agent").RequireAuthorization(UserPolicy);
        return app;
    }

    private static async Task<IResult> Upload(HttpRequest request, AgentPackageService pkg, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Ожидается multipart/form-data с полем file." });
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Файл не передан." });
        try
        {
            using var stream = file.OpenReadStream();
            return Results.Ok(await pkg.PublishAsync(stream, ct));
        }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// PowerShell-скрипт установки. Совместим с PowerShell 2.0 (Server 2008 R2): без Invoke-WebRequest и Expand-Archive.
    /// Использование: install.ps1 -Token XXX [-Allow "ping,..."] [-Dir "C:\Program Files\Sentinel\Agent"]
    /// </summary>
    private static string InstallScript(string publicUrl) => $@"param(
  [Parameter(Mandatory=$true)][string]$Token,
  [string]$Allow = 'ping,config.refresh,agent.update,service.*,backup.*,cctv.*',
  [string]$Server = '{publicUrl.TrimEnd('/')}',
  [string]$Pin = '',
  [string]$Dir = ""$env:ProgramFiles\Sentinel\Agent""
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {{
  Write-Host 'Запустите PowerShell от имени администратора.' -ForegroundColor Red; exit 1
}}
try {{ [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 }} catch {{ }}   # TLS 1.2
if ($Pin) {{ [Net.ServicePointManager]::ServerCertificateValidationCallback = {{ $true }} }}   # сервер по IP с внутренним CA: агент дальше доверяет по pin
$zip = Join-Path $env:TEMP 'SentinelAgent.zip'
Write-Host ""Скачиваю $Server/agent/SentinelAgent.zip ...""
(New-Object Net.WebClient).DownloadFile(""$Server/agent/SentinelAgent.zip"", $zip)
if (Test-Path ""$Dir\SentinelAgent.exe"") {{ & ""$Dir\SentinelAgent.exe"" uninstall | Out-Null }}
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$shell = New-Object -ComObject Shell.Application
$shell.NameSpace($Dir).CopyHere($shell.NameSpace($zip).Items(), 0x14)   # 0x14 = без диалогов, перезаписывать
Remove-Item $zip -Force
$pinArg = @(); if ($Pin) {{ $pinArg = @('--pin', $Pin) }}
& ""$Dir\SentinelAgent.exe"" enroll --server $Server --token $Token --allow $Allow @pinArg
if ($LASTEXITCODE -ne 0) {{ Write-Host 'Регистрация не удалась.' -ForegroundColor Red; exit $LASTEXITCODE }}
& ""$Dir\SentinelAgent.exe"" install
Write-Host 'Готово. Агент установлен как служба SentinelAgent.' -ForegroundColor Green
";
}
