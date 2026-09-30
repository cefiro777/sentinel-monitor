# Сборка всех дистрибутивов Sentinel для Windows:
#   artifacts\SentinelAgent-<v>.zip             — пакет агента для публикации в дашборде (автообновление агентов)
#   artifacts\SentinelAgent-Setup-<v>.exe       — офлайн-установщик агента (+ .NET Framework 4.8)
#   artifacts\SentinelServer-Setup-<v>.exe      — установщик сервера для Windows (+ PostgreSQL 16, VC++ Redistributable)
#
# Нужны: .NET SDK 10, .NET Framework 4.8 Developer Pack, Node.js 20+, Inno Setup 6 (ISCC.exe).
# Редистрибутивы скачиваются один раз в installers\redist (в git не попадают).
#
#   .\installers\build.ps1                      # версия из Directory.Build.props
#   .\installers\build.ps1 -Version 0.4.0 -Only agent
param(
    [string]$Version,
    [ValidateSet('all', 'agent', 'server')][string]$Only = 'all'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$redist = Join-Path $PSScriptRoot 'redist'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $redist, $artifacts | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1 }
Write-Host "== Sentinel $Version ==" -ForegroundColor Cyan

$PostgresVersion = '16.15-1'
$Downloads = @{
    'ndp48-x86-x64-allos-enu.exe' = 'https://go.microsoft.com/fwlink/?linkid=2088631'
    'vc_redist.x64.exe'           = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
    "postgresql-$PostgresVersion-windows-x64-binaries.zip" = "https://get.enterprisedb.com/postgresql/postgresql-$PostgresVersion-windows-x64-binaries.zip"
}

function Fetch([string]$name) {
    $path = Join-Path $redist $name
    if (Test-Path $path) { return $path }
    Write-Host "Скачиваю $name ..."
    $tmp = "$path.part"
    Invoke-WebRequest -Uri $Downloads[$name] -OutFile $tmp -UseBasicParsing
    Move-Item $tmp $path
    return $path
}

function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw 'Не найден Inno Setup 6 (ISCC.exe): https://jrsoftware.org/isdl.php или choco install innosetup'
}

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe завершился с кодом $LASTEXITCODE" }
}

$iscc = Find-Iscc

if ($Only -in 'all', 'agent') {
    Write-Host '-- агент' -ForegroundColor Cyan
    & (Join-Path $root 'deploy\build-agent.ps1') -Version $Version
    Fetch 'ndp48-x86-x64-allos-enu.exe' | Out-Null
    Run $iscc @("/Q", "/DAppVersion=$Version", "/DAgentDir=$artifacts\agent-$Version", "/DRedistDir=$redist", "/DOutputDir=$artifacts",
        (Join-Path $PSScriptRoot 'agent\SentinelAgent.iss'))
}

if ($Only -in 'all', 'server') {
    Write-Host '-- сервер для Windows' -ForegroundColor Cyan
    # PostgreSQL: из архива EDB берём только bin/lib/share — без pgAdmin, StackBuilder, символов и документации.
    $pgRoot = Join-Path $redist 'pgsql'
    $pgMarker = Join-Path $pgRoot ".version-$PostgresVersion"
    if (-not (Test-Path $pgMarker)) {
        $zip = Fetch "postgresql-$PostgresVersion-windows-x64-binaries.zip"
        if (Test-Path $pgRoot) { Remove-Item $pgRoot -Recurse -Force }
        Write-Host 'Распаковываю PostgreSQL (bin, lib, share) ...'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            foreach ($e in $archive.Entries) {
                if ($e.FullName -notmatch '^pgsql/(bin|lib|share)/' -or $e.FullName.EndsWith('/')) { continue }
                if ($e.FullName -match '\.pdb$|^pgsql/bin/(pgAdmin|stackbuilder)' ) { continue }
                $dest = Join-Path $pgRoot ($e.FullName.Substring(6) -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
            }
        }
        finally { $archive.Dispose() }
        Set-Content $pgMarker $PostgresVersion
    }
    Fetch 'vc_redist.x64.exe' | Out-Null

    Push-Location (Join-Path $root 'src\Sentinel.Web')
    try {
        if (-not (Test-Path node_modules)) { Run 'npm' @('ci') }
        Run 'npm' @('run', 'build')
    }
    finally { Pop-Location }

    $serverOut = Join-Path $artifacts 'server-win-x64'
    if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
    Run 'dotnet' @('publish', (Join-Path $root 'src\Sentinel.Server\Sentinel.Server.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:Version=$Version", '-o', $serverOut, '--nologo', '-v', 'q')
    Get-ChildItem $serverOut -Filter *.pdb | Remove-Item -Force
    $wwwroot = Join-Path $serverOut 'wwwroot'
    if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }   # локальная сборка дашборда могла попасть в publish
    Copy-Item (Join-Path $root 'src\Sentinel.Web\dist') (Join-Path $serverOut 'wwwroot') -Recurse -Force
    Run $iscc @("/Q", "/DAppVersion=$Version", "/DServerDir=$serverOut", "/DRedistDir=$redist", "/DOutputDir=$artifacts",
        (Join-Path $PSScriptRoot 'server-windows\SentinelServer.iss'))
}

Get-ChildItem $artifacts -File | Where-Object { $_.Name -like "*$Version*" } |
    ForEach-Object { '{0,-45} {1,8:N1} МБ' -f $_.Name, ($_.Length / 1MB) }
