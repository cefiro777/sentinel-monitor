# Настройка сервера Sentinel на Windows — вызывается установщиком (SentinelServer-Setup.exe) от администратора.
#
# Все шаги идемпотентны: первая установка, обновление и переустановка поверх сохранённых данных идут одним путём —
# создаётся только то, чего нет (кластер PostgreSQL, база, настройки, службы, правило брандмауэра, задача бэкапа).
# Данные и настройки живут в %ProgramData%\Sentinel (Server — ключи, настройки, логи, бэкапы; PostgreSQL — кластер БД)
# и не удаляются вместе с программой.
# Каждый шаг пишется в %ProgramData%\Sentinel\Server\setup.log; у каждой внешней команды — таймаут, зависаний нет.
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [string]$PublicHost = $env:COMPUTERNAME,
    [int]$HttpsPort = 443,
    [string]$AdminEmail = 'admin@local',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ServerSvc = 'SentinelServer'
$PgSvc = 'SentinelPostgreSQL'
$FirewallRule = 'Sentinel Server (HTTPS)'
$BackupTask = 'Sentinel Server Backup'
$pgBin = Join-Path $InstallDir 'pgsql\bin'
# Кластер БД — рядом с данными сервера, но отдельной папкой: у служб разные учётки и права.
$pgData = Join-Path (Split-Path $DataDir -Parent) 'PostgreSQL'
$serverExe = Join-Path $InstallDir 'server\Sentinel.Server.exe'
$configFile = Join-Path $DataDir 'appsettings.json'
$logFile = Join-Path $DataDir 'setup.log'
$account = "NT SERVICE\$ServerSvc"
$env:LC_MESSAGES = 'C'   # сообщения утилит PostgreSQL — по-английски, без кракозябр OEM-кодировки в журнале

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
function Log([string]$m) { $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m"; Write-Host $line; Add-Content -Path $logFile -Value $line -Encoding UTF8 }

# Запуск внешней программы с таймаутом и выводом в журнал (без конвейеров PowerShell — они умеют зависать на чужих дескрипторах).
function Run([string]$exe, [string[]]$argv, [int]$timeoutSec = 120, [switch]$NoThrow) {
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    $psi.Arguments = ($argv | ForEach-Object { if ($_ -eq '' -or $_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $name = [IO.Path]::GetFileNameWithoutExtension($exe)
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        throw "$name не завершился за $timeoutSec с"
    }
    foreach ($t in @($out, $err)) {
        if ($t.Wait(3000)) { foreach ($l in ($t.Result -split "`r?`n")) { if ($l.Trim()) { Log "  ${name}: $l" } } }
    }
    if (-not $NoThrow -and $p.ExitCode -ne 0) { throw "$name $($argv[0]) завершился с кодом $($p.ExitCode)" }
    return $p.ExitCode
}
# Не «Sc»: в PowerShell это встроенный псевдоним Set-Content, он сильнее функции (и молча ждёт ввода).
function ScExe([string[]]$argv, [switch]$NoThrow) { Run "$env:SystemRoot\System32\sc.exe" $argv 60 -NoThrow:$NoThrow }
function ServiceExists([string]$n) { return [bool](Get-Service -Name $n -ErrorAction SilentlyContinue) }
function StartSvc([string]$n, [int]$timeoutSec = 90) {
    if ((Get-Service $n).Status -eq 'Running') { Log "Служба $n уже работает"; return }
    Log "Запуск службы $n"
    ScExe @('start', $n) -NoThrow | Out-Null
    for ($i = 0; $i -lt $timeoutSec; $i++) {
        $s = (Get-Service $n).Status
        if ($s -eq 'Running') { Log "Служба $n запущена"; return }
        if ($s -eq 'Stopped' -and $i -gt 5) { break }
        Start-Sleep -Seconds 1
    }
    throw "Служба $n не запустилась (состояние $((Get-Service $n).Status)). Смотрите $DataDir\logs, журнал событий Windows → Приложение, для PostgreSQL — $pgData\log"
}
function NewSecret {
    $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    return [Convert]::ToBase64String($b) -replace '[+/=]', ''
}
# Группы — по SID (на русской Windows нет имени BUILTIN\Administrators): S-1-5-32-544 Администраторы, S-1-5-18 SYSTEM, S-1-5-20 NETWORK SERVICE.
function Acl([string]$path, [string[]]$argv) { Run "$env:SystemRoot\System32\icacls.exe" (@($path) + $argv + @('/Q')) 300 | Out-Null }
function AdminsOnly([string]$path, [string[]]$extra = @()) { Acl $path (@('/inheritance:r', '/grant:r', '*S-1-5-32-544:F', '*S-1-5-18:F') + $extra) }
function FreePort([int]$start) {
    for ($p = $start; $p -lt $start + 50; $p++) {
        if (-not (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)) { return $p }
    }
    throw "Нет свободного порта для PostgreSQL в диапазоне $start–$($start + 49)"
}
function ReadConfig { return Get-Content $configFile -Raw -Encoding UTF8 | ConvertFrom-Json }
function PgPortFromConfig { if ((ReadConfig).ConnectionStrings.Default -match 'Port=(\d+)') { return [int]$Matches[1] }; return 5432 }

try {
    # ------------------------------------------------------------ удаление (данные и настройки остаются)
    if ($Uninstall) {
        Log 'Удаление служб Sentinel (данные и настройки в ProgramData сохраняются)'
        foreach ($s in @($ServerSvc, $PgSvc)) {
            if (ServiceExists $s) { ScExe @('stop', $s) -NoThrow | Out-Null; Start-Sleep -Seconds 3; ScExe @('delete', $s) -NoThrow | Out-Null }
        }
        Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        Unregister-ScheduledTask -TaskName $BackupTask -Confirm:$false -ErrorAction SilentlyContinue
        exit 0
    }

    # ------------------------------------------------------------ PostgreSQL: кластер
    $newCluster = -not (Test-Path (Join-Path $pgData 'PG_VERSION'))
    $dbPassword = ''
    if ($newCluster) {
        if (Test-Path $configFile) { throw "Есть настройки $configFile, но нет кластера PostgreSQL в $pgData. Восстановите кластер из бэкапа или удалите $configFile для чистой установки." }
        $pgPort = FreePort 5432   # 5432 часто занят PostgreSQL для 1С — тогда берём следующий свободный
        $superPassword = NewSecret
        $dbPassword = NewSecret
        Log "PostgreSQL: инициализация кластера в $pgData (порт $pgPort)"
        New-Item -ItemType Directory -Force -Path $pgData | Out-Null
        $pwFile = Join-Path $env:TEMP "sentinel-pg-$([guid]::NewGuid()).txt"
        [IO.File]::WriteAllText($pwFile, $superPassword)
        try { Run "$pgBin\initdb.exe" @('-D', $pgData, '-U', 'postgres', '-A', 'scram-sha-256', "--pwfile=$pwFile", '-E', 'UTF8', '--locale=C') 300 | Out-Null }
        finally { Remove-Item $pwFile -Force -ErrorAction SilentlyContinue }
        Add-Content -Path (Join-Path $pgData 'postgresql.conf') -Encoding ASCII -Value @"

# --- Sentinel ---
listen_addresses = 'localhost'
port = $pgPort
max_connections = 50
shared_buffers = 256MB
logging_collector = on
log_directory = 'log'
log_filename = 'postgresql-%a.log'
log_truncate_on_rotation = on
"@
        # Служба PostgreSQL работает от NetworkService — ей нужен полный доступ к кластеру.
        Log 'PostgreSQL: права NetworkService на каталог кластера'
        Acl $pgData @('/grant', '*S-1-5-20:(OI)(CI)F', '/T')
    }
    else {
        if (-not (Test-Path $configFile)) { throw "Кластер PostgreSQL есть в $pgData, но нет настроек $configFile. Восстановите файл из бэкапа (keys_*.zip) или удалите $pgData для чистой установки." }
        $pgPort = PgPortFromConfig
        Log "PostgreSQL: существующий кластер $pgData (порт $pgPort)"
    }

    # ------------------------------------------------------------ PostgreSQL: служба
    if (-not (ServiceExists $PgSvc)) {
        Log 'PostgreSQL: регистрация службы'
        Run "$pgBin\pg_ctl.exe" @('register', '-N', $PgSvc, '-U', 'NT AUTHORITY\NetworkService', '-D', $pgData, '-S', 'auto') 60 | Out-Null
        ScExe @('description', $PgSvc, 'База данных сервера мониторинга Sentinel') -NoThrow | Out-Null
        ScExe @('failure', $PgSvc, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000') -NoThrow | Out-Null
    }
    StartSvc $PgSvc

    # ------------------------------------------------------------ база и настройки (только для нового кластера)
    if ($newCluster) {
        $env:PGPASSWORD = $superPassword
        for ($i = 0; $i -lt 30; $i++) {
            if ((Run "$pgBin\pg_isready.exe" @('-h', 'localhost', '-p', "$pgPort", '-q') 10 -NoThrow) -eq 0) { break }
            Start-Sleep -Seconds 1
        }
        Log 'PostgreSQL: создание базы и пользователя sentinel'
        Run "$pgBin\psql.exe" @('-h', 'localhost', '-p', "$pgPort", '-U', 'postgres', '-d', 'postgres', '-w', '-v', 'ON_ERROR_STOP=1',
            '-c', "CREATE ROLE sentinel LOGIN PASSWORD '$dbPassword';") 60 | Out-Null
        Run "$pgBin\psql.exe" @('-h', 'localhost', '-p', "$pgPort", '-U', 'postgres', '-d', 'postgres', '-w', '-v', 'ON_ERROR_STOP=1',
            '-c', "CREATE DATABASE sentinel OWNER sentinel ENCODING 'UTF8' TEMPLATE template0;") 60 | Out-Null
        Remove-Item Env:\PGPASSWORD
        # Пароль суперпользователя нужен только для обслуживания — рядом с данными, доступ только администраторам.
        $superFile = Join-Path $DataDir 'postgres-superuser.txt'
        Set-Content -Path $superFile -Value "postgres / $superPassword (порт $pgPort)" -Encoding UTF8
        AdminsOnly $superFile

        $publicUrl = if ($HttpsPort -eq 443) { "https://$PublicHost" } else { "https://${PublicHost}:$HttpsPort" }
        Log "Настройки: $publicUrl → $configFile"
        $config = [ordered]@{
            Urls              = "https://*:$HttpsPort"
            ConnectionStrings = [ordered]@{ Default = "Host=localhost;Port=$pgPort;Database=sentinel;Username=sentinel;Password=$dbPassword" }
            Sentinel          = [ordered]@{
                DataDir   = $DataDir
                PublicUrl = $publicUrl
                LogToFile = $true
                Tls       = [ordered]@{ Mode = 'self-signed' }
                Bootstrap = [ordered]@{ AdminEmail = $AdminEmail }
            }
        }
        [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    }
    $port = if ((ReadConfig).Urls -match ':(\d+)') { [int]$Matches[1] } else { 443 }
    Set-Content -Path (Join-Path $DataDir 'dashboard-url.txt') -Value (ReadConfig).Sentinel.PublicUrl -Encoding ASCII

    # ------------------------------------------------------------ служба сервера
    if (-not (ServiceExists $ServerSvc)) {
        Log 'Регистрация службы SentinelServer'
        ScExe @('create', $ServerSvc, 'binPath=', "`"$serverExe`" --config `"$configFile`"", 'start=', 'delayed-auto', 'depend=', $PgSvc,
            'obj=', $account, 'DisplayName=', 'Sentinel Monitoring Server') | Out-Null
        ScExe @('description', $ServerSvc, 'Сервер мониторинга Sentinel: дашборд, API, связь с агентами') -NoThrow | Out-Null
        ScExe @('failure', $ServerSvc, 'reset=', '86400', 'actions=', 'restart/10000/restart/30000/restart/60000') -NoThrow | Out-Null
    }
    # Виртуальная учётка службы пишет только в DataDir и читает свои настройки (в них пароль БД — остальным не виден).
    Log 'Права службы SentinelServer'
    Acl $DataDir @('/grant', "${account}:(OI)(CI)M", '/T')
    $backups = Join-Path $DataDir 'backups'   # в бэкапах ключи сервера — только администраторам (пишет задача от SYSTEM)
    New-Item -ItemType Directory -Force -Path $backups | Out-Null
    Acl $backups @('/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F')
    AdminsOnly $configFile @("${account}:R")

    # ------------------------------------------------------------ брандмауэр
    Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Any | Out-Null
    Log "Брандмауэр: открыт входящий TCP $port"

    # ------------------------------------------------------------ ежедневный бэкап
    $backupScript = Join-Path $InstallDir 'tools\backup.ps1'
    if (Test-Path $backupScript) {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$backupScript`" -InstallDir `"$InstallDir`" -DataDir `"$DataDir`""
        $trigger = New-ScheduledTaskTrigger -Daily -At 3am
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $BackupTask -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
        Log "Бэкап: задача «$BackupTask» ежедневно в 03:00"
    }

    StartSvc $ServerSvc

    # Первый запуск: миграции БД и создание администратора (пароль — в initial-admin-password.txt).
    if ($newCluster) {
        $pwdFile = Join-Path $DataDir 'initial-admin-password.txt'
        for ($i = 0; $i -lt 90 -and -not (Test-Path $pwdFile); $i++) { Start-Sleep -Seconds 1 }
        if (Test-Path $pwdFile) { AdminsOnly $pwdFile; Log 'Готово: пароль администратора в initial-admin-password.txt' }
        else { Log "Сервер не создал initial-admin-password.txt за 90 с — смотрите $DataDir\logs" }
    }
    # Сервер слушает порт уже после миграций — ждём, чтобы установщик открыл дашборд не впустую.
    for ($i = 0; $i -lt 60; $i++) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { Log "Сервер принимает подключения на порту $port"; break }
        Start-Sleep -Seconds 1
    }
    exit 0
}
catch {
    Log "ОШИБКА: $($_.Exception.Message) (строка $($_.InvocationInfo.ScriptLineNumber))"
    exit 1
}
