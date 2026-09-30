; Установщик сервера Sentinel для Windows Server 2016+ / Windows 10+ (x64) — без Docker.
; Внутри: сервер (self-contained .NET, дашборд), PostgreSQL 16, Visual C++ Redistributable.
; Ставит службы SentinelPostgreSQL и SentinelServer, HTTPS с самоподписанным сертификатом (агенты доверяют по pin),
; правило брандмауэра и ежедневный бэкап. Повторный запуск новой версии — обновление с сохранением данных.
;
; Сборка: installers\build.ps1
; Тихая установка: SentinelServer-Setup.exe /VERYSILENT /HOST=monitor.corp.local /PORT=443 /ADMIN=admin@corp.local

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef ServerDir
  #define ServerDir "..\..\artifacts\server-win-x64"
#endif
#ifndef RedistDir
  #define RedistDir "..\redist"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
AppId={{C3A9E1D4-7B2F-4C6A-8E5D-2F1A0B9C8D71}
AppName=Sentinel Server
AppVersion={#AppVersion}
AppVerName=Sentinel Server {#AppVersion}
AppPublisher=Sentinel Monitor
AppPublisherURL=https://github.com/cefiro777/sentinel-monitor
AppSupportURL=https://github.com/cefiro777/sentinel-monitor/issues
DefaultDirName={commonpf}\Sentinel\Server
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=SentinelServer-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Sentinel Server
UninstallDisplayIcon={app}\server\Sentinel.Server.exe
CloseApplications=no
SetupLogging=yes
LicenseFile=..\..\LICENSE

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#ServerDir}\*"; DestDir: "{app}\server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RedistDir}\pgsql\*"; DestDir: "{app}\pgsql"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "setup-server.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "backup.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#RedistDir}\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall dontcopy

[Icons]
Name: "{commondesktop}\Sentinel"; Filename: "{code:DashboardUrl}"; Check: not IsUpgrade

[Run]
Filename: "{code:DashboardUrl}"; Description: "Открыть дашборд Sentinel"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\setup-server.ps1"" -InstallDir ""{app}"" -DataDir ""{commonappdata}\Sentinel\Server"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveServices"

[Messages]
ru.WelcomeLabel2=Будет установлен сервер мониторинга Sentinel {#AppVersion}: дашборд, API и приём данных от агентов.%n%nВместе с ним ставятся PostgreSQL 16 (только для локального доступа) и Visual C++ Redistributable. Интернет для установки не нужен.%n%nДанные и настройки хранятся в %ProgramData%\Sentinel и сохраняются при обновлении и удалении программы — повторная установка их подхватит.
ru.FinishedLabel=Сервер Sentinel установлен и запущен.

[Code]
var
  ConfigPage: TInputQueryWizardPage;
  AdminPassword: String;
  WasInstalled: Boolean;

// Запоминаем до установки: после неё файл настроек есть всегда.
function InitializeSetup: Boolean;
begin
  // Настройки лежат рядом с данными и переживают удаление программы: есть они — это обновление или переустановка.
  WasInstalled := FileExists(ExpandConstant('{commonappdata}\Sentinel\Server\appsettings.json'));
  Result := True;
end;

function IsUpgrade: Boolean;
begin
  Result := WasInstalled;
end;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\Sentinel\Server');
end;

function DefaultHost: String;
var
  Domain: String;
begin
  Result := GetComputerNameString;
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\Tcpip\Parameters', 'Domain', Domain) and (Domain <> '') then
    Result := Result + '.' + Domain;
  Result := Lowercase(Result);
end;

function PublicHost: String;
begin
  Result := Trim(ConfigPage.Values[0]);
end;

function HttpsPort: String;
begin
  Result := Trim(ConfigPage.Values[1]);
end;

function DashboardUrl(Param: String): String;
var
  Url: AnsiString;
begin
  // Адрес из настроек пишет setup-server.ps1 — при обновлении страница параметров пропускается.
  if IsUpgrade and LoadStringFromFile(DataDir + '\dashboard-url.txt', Url) then
    Result := Trim(String(Url))
  else if HttpsPort = '443' then
    Result := 'https://' + PublicHost
  else
    Result := 'https://' + PublicHost + ':' + HttpsPort;
end;

procedure InitializeWizard;
begin
  ConfigPage := CreateInputQueryPage(wpSelectDir,
    'Параметры сервера',
    'По этому адресу агенты и инженеры будут подключаться к серверу.',
    'Адрес — DNS-имя или IP, доступный с серверов клиентов (если агенты в интернете — внешний адрес или домен с пробросом порта). ' +
    'Сертификат HTTPS будет самоподписанным: браузер предупредит один раз, агенты доверяют ему по отпечатку. ' +
    'Свой сертификат (pfx) можно подключить позже — см. документацию.');
  ConfigPage.Add('Адрес сервера (имя или IP):', False);
  ConfigPage.Add('Порт HTTPS:', False);
  ConfigPage.Add('Email администратора (логин):', False);
  ConfigPage.Values[0] := ExpandConstant('{param:HOST|' + DefaultHost + '}');
  ConfigPage.Values[1] := ExpandConstant('{param:PORT|443}');
  ConfigPage.Values[2] := ExpandConstant('{param:ADMIN|admin@local}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Обновление: адрес, порт и администратор уже в настройках — не спрашиваем.
  Result := (PageID = ConfigPage.ID) and IsUpgrade;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then
  begin
    Port := StrToIntDef(HttpsPort, 0);
    if PublicHost = '' then
    begin
      MsgBox('Укажите адрес сервера.', mbError, MB_OK); Result := False;
    end
    else if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Порт HTTPS — число от 1 до 65535.', mbError, MB_OK); Result := False;
    end
    else if Pos('@', ConfigPage.Values[2]) = 0 then
    begin
      MsgBox('Укажите email администратора — это логин для входа в дашборд.', mbError, MB_OK); Result := False;
    end;
  end;
end;

function VcRedistInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1);
end;

// Обновление: службы держат файлы — останавливаем до копирования.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  // net stop ждёт полной остановки (sc stop — нет). PostgreSQL держит свои DLL в {app}\pgsql — её тоже останавливаем,
  // setup-server.ps1 после копирования запустит обе службы.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop SentinelServer /y', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop SentinelPostgreSQL /y', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not VcRedistInstalled then
  begin
    WizardForm.PreparingLabel.Caption := 'Устанавливается Visual C++ Redistributable…';
    ExtractTemporaryFile('vc_redist.x64.exe');
    if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      Result := 'Не удалось запустить установку Visual C++ Redistributable.'
    else if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
      Result := 'Установка Visual C++ Redistributable завершилась с кодом ' + IntToStr(Code) + '.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args, PwdFile: String;
  Code: Integer;
  Lines: TArrayOfString;
  I: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Настройка PostgreSQL и служб Sentinel (1–2 минуты)…';
  Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\tools\setup-server.ps1') + '"' +
    ' -InstallDir "' + ExpandConstant('{app}') + '" -DataDir "' + DataDir + '"';
  if not IsUpgrade then
    Args := Args + ' -PublicHost "' + PublicHost + '" -HttpsPort ' + HttpsPort + ' -AdminEmail "' + Trim(ConfigPage.Values[2]) + '"';
  Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code <> 0 then
  begin
    SuppressibleMsgBox('Настройка сервера завершилась с ошибкой (код ' + IntToStr(Code) + ').' + #13#10 +
      'Журнал: ' + DataDir + '\setup.log', mbError, MB_OK, IDOK);
    Exit;
  end;

  PwdFile := DataDir + '\initial-admin-password.txt';
  AdminPassword := '';
  if (not IsUpgrade) and LoadStringsFromFile(PwdFile, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      AdminPassword := AdminPassword + Lines[I] + #13#10;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (AdminPassword <> '') then
    WizardForm.FinishedLabel.Caption := 'Сервер Sentinel установлен и запущен: ' + DashboardUrl('') + #13#10#13#10 +
      AdminPassword + #13#10 +
      'Пароль сохранён в ' + DataDir + '\initial-admin-password.txt (доступ только администраторам).';
end;
