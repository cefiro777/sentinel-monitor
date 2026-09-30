# Собирает дистрибутив агента: dotnet publish (net48, Release) -> artifacts/SentinelAgent-<version>.zip с version.txt внутри.
# Использование: .\deploy\build-agent.ps1 -Version 0.2.0
# Затем загрузите zip на странице «Установка агентов» (или POST /api/agent-package) — сервер подпишет его и раздаст агентам.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\agent-$Version"
$zip = Join-Path $root "artifacts\SentinelAgent-$Version.zip"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\src\Sentinel.Agent\Sentinel.Agent.csproj" -c Release -o $out -p:Version=$Version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force
Get-ChildItem $out -Filter *.xml | Remove-Item -Force
Set-Content -Path (Join-Path $out 'version.txt') -Value $Version -Encoding ascii
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip)
Write-Host "Готово: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) МБ)"
