# Ежедневный бэкап сервера Sentinel на Windows (задача планировщика «Sentinel Server Backup», от SYSTEM).
# Дамп БД + ключи (data.key, jwt.key, signing.key) + сертификат и настройки (appsettings.json) -> DataDir\backups, хранится 14 копий.
# Копируйте папку backups наружу: без signing.key и data.key все агенты придётся регистрировать заново.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [int]$Keep = 14
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $DataDir 'backups'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$ts = Get-Date -Format 'yyyyMMdd_HHmm'

$config = Get-Content (Join-Path $DataDir 'appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$cs = @{}
foreach ($part in $config.ConnectionStrings.Default.Split(';')) { $kv = $part.Split('=', 2); if ($kv.Count -eq 2) { $cs[$kv[0].Trim()] = $kv[1] } }

$env:PGPASSWORD = $cs['Password']
& (Join-Path $InstallDir 'pgsql\bin\pg_dump.exe') -h $cs['Host'] -p $cs['Port'] -U $cs['Username'] -d $cs['Database'] -Fc -f (Join-Path $out "sentinel_$ts.dump")
if ($LASTEXITCODE -ne 0) { throw "pg_dump завершился с кодом $LASTEXITCODE" }
Remove-Item Env:\PGPASSWORD

$staging = Join-Path $env:TEMP "sentinel-keys-$ts"
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($item in @('keys', 'tls', 'dataprotection')) {
    $src = Join-Path $DataDir $item
    if (Test-Path $src) { Copy-Item $src -Destination $staging -Recurse -Force }
}
Copy-Item (Join-Path $DataDir 'appsettings.json') -Destination $staging -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, (Join-Path $out "keys_$ts.zip"))
Remove-Item $staging -Recurse -Force

foreach ($pattern in @('sentinel_*.dump', 'keys_*.zip')) {
    Get-ChildItem $out -Filter $pattern | Sort-Object LastWriteTime -Descending | Select-Object -Skip $Keep | Remove-Item -Force
}
Write-Host "backup $ts ok"
