; Офлайн-установщик агента Sentinel для Windows Server 2008 R2 SP1 / Windows 7 SP1 и новее.
; Внутри: агент + .NET Framework 4.8 (ставится, только если его нет). Интернет на сервере клиента не нужен —
; только связь с сервером Sentinel.
;
; Сборка: installers\build.ps1 (или вручную: ISCC /DAppVersion=0.3.7 /DAgentDir=... /DRedistDir=... SentinelAgent.iss)
;
; Тихая установка (для GPO/RMM):
;   SentinelAgent-Setup.exe /VERYSILENT /SERVER=https://monitor.example.com /TOKEN=<токен>
;     [/PROXY=none|system|http://host:3128] [/PIN=<base64>] [/ALLOW=ping,config.refresh,agent.update,...]
; Обновление поверх установленного агента: тот же exe без /TOKEN — регистрация сохраняется.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AgentDir
  #define AgentDir "..\..\artifacts\agent-" + AppVersion
#endif
#ifndef RedistDir
  #define RedistDir "..\redist"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define DefaultAllow "ping,config.refresh,agent.update,service.*,backup.*,cctv.*"

[Setup]
AppId={{6B1E3F2A-5C4D-4E8B-9A7F-1D2C3B4A5E60}
AppName=Sentinel Agent
AppVersion={#AppVersion}
AppVerName=Sentinel Agent {#AppVersion}
AppPublisher=Sentinel Monitor
AppPublisherURL=https://github.com/cefiro777/sentinel-monitor
AppSupportURL=https://github.com/cefiro777/sentinel-monitor/issues
DefaultDirName={commonpf}\Sentinel\Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
ArchitecturesAllowed=x64compatible or x86compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=SentinelAgent-Setup-{#AppVersion}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Sentinel Agent
UninstallDisplayIcon={app}\SentinelAgent.exe
CloseApplications=no
SetupLogging=yes
LicenseFile=..\..\LICENSE

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#AgentDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RedistDir}\ndp48-x86-x64-allos-enu.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall dontcopy

[UninstallRun]
Filename: "{app}\SentinelAgent.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Messages]
ru.WelcomeLabel2=Будет установлен агент мониторинга Sentinel {#AppVersion}.%n%nПонадобятся адрес сервера Sentinel и токен регистрации — их выдаёт страница «Установка агентов» в дашборде.%n%nЕсли на сервере нет .NET Framework 4.8, он будет установлен автоматически (может потребоваться перезагрузка).

[Code]
var
  ServerPage: TInputQueryWizardPage;
  AdvancedPage: TInputQueryWizardPage;
  RestartAfterNetFx: Boolean;

function IdentityFile: String;
begin
  Result := ExpandConstant('{commonappdata}\Sentinel\Agent\agent.json');
end;

function IsEnrolled: Boolean;
begin
  Result := FileExists(IdentityFile);
end;

function NetFx48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function Param(const Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + Default + '}');
end;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(wpWelcome,
    'Подключение к серверу Sentinel',
    'Адрес сервера и одноразовый токен регистрации.',
    'Токен создаётся в дашборде: «Установка агентов» → выбрать клиента → «Создать токен». ' +
    'Если агент уже был зарегистрирован на этом компьютере, токен можно оставить пустым — установка обновит файлы и сохранит регистрацию.');
  ServerPage.Add('Адрес сервера (https://…):', False);
  ServerPage.Add('Токен регистрации:', False);
  ServerPage.Values[0] := Param('SERVER', '');
  ServerPage.Values[1] := Param('TOKEN', '');

  AdvancedPage := CreateInputQueryPage(ServerPage.ID,
    'Дополнительно',
    'Обычно эти поля можно оставить как есть.',
    'Прокси: пусто или system — системный прокси; none — напрямую; или адрес http://host:3128. ' +
    'Pin — отпечаток сертификата сервера, если он работает по IP с самоподписанным сертификатом (сервер выдаёт его сам, заполнять не обязательно). ' +
    'Разрешённые команды — какие удалённые действия сервер сможет выполнять на этом компьютере.');
  AdvancedPage.Add('Прокси:', False);
  AdvancedPage.Add('Pin сертификата (base64):', False);
  AdvancedPage.Add('Разрешённые команды:', False);
  AdvancedPage.Values[0] := Param('PROXY', '');
  AdvancedPage.Values[1] := Param('PIN', '');
  AdvancedPage.Values[2] := Param('ALLOW', '{#DefaultAllow}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url: String;
begin
  Result := True;
  if CurPageID = ServerPage.ID then
  begin
    Url := Trim(ServerPage.Values[0]);
    if (Trim(ServerPage.Values[1]) = '') and IsEnrolled then Exit;
    if (Pos('https://', Lowercase(Url)) <> 1) and (Pos('http://', Lowercase(Url)) <> 1) then
    begin
      MsgBox('Укажите адрес сервера, начиная с https:// (например, https://monitor.example.com).', mbError, MB_OK);
      Result := False;
    end
    else if Trim(ServerPage.Values[1]) = '' then
    begin
      MsgBox('Укажите токен регистрации. Он создаётся в дашборде на странице «Установка агентов».', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
end;

// Служба держит exe и dll — останавливаем до копирования файлов.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  // net stop ждёт полной остановки службы (sc stop — нет), иначе exe и dll ещё заняты.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop SentinelAgent /y', '', SW_HIDE, ewWaitUntilTerminated, Code);

  if not NetFx48Installed then
  begin
    WizardForm.PreparingLabel.Caption := 'Устанавливается .NET Framework 4.8 — это может занять 5–15 минут…';
    ExtractTemporaryFile('ndp48-x86-x64-allos-enu.exe');
    if not Exec(ExpandConstant('{tmp}\ndp48-x86-x64-allos-enu.exe'), '/q /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      Result := 'Не удалось запустить установку .NET Framework 4.8.'
    else if (Code = 1641) or (Code = 3010) then
      RestartAfterNetFx := True
    else if Code <> 0 then
      Result := 'Установка .NET Framework 4.8 завершилась с кодом ' + IntToStr(Code) + '.' + #13#10 +
        'На Windows Server 2008 R2 / Windows 7 нужен SP1 и обновления поддержки SHA-2 (KB4474419, KB4490628). ' +
        'Установите их и запустите установщик агента снова.';
  end;
end;

function RunAgent(const Args, LogName: String): Integer;
var
  Log: String;
begin
  Log := ExpandConstant('{tmp}\') + LogName;
  if not Exec(ExpandConstant('{cmd}'), '/c ""' + ExpandConstant('{app}\SentinelAgent.exe') + '" ' + Args + ' > "' + Log + '" 2>&1"',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  if Result <> 0 then
  begin
    FileCopy(Log, ExpandConstant('{commonappdata}\Sentinel\Agent\') + LogName, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Args, Server, Token, Proxy, Pin, Allow: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  ForceDirectories(ExpandConstant('{commonappdata}\Sentinel\Agent'));

  Server := Trim(ServerPage.Values[0]);
  Token := Trim(ServerPage.Values[1]);
  Proxy := Trim(AdvancedPage.Values[0]);
  Pin := Trim(AdvancedPage.Values[1]);
  Allow := Trim(AdvancedPage.Values[2]);

  if Token <> '' then
  begin
    WizardForm.StatusLabel.Caption := 'Регистрация на сервере Sentinel…';
    Args := 'enroll --server "' + Server + '" --token "' + Token + '"';
    if Allow <> '' then Args := Args + ' --allow "' + Allow + '"';
    if Pin <> '' then Args := Args + ' --pin "' + Pin + '"';
    if Proxy <> '' then Args := Args + ' --proxy "' + Proxy + '"';
    Code := RunAgent(Args, 'setup-enroll.log');
    if Code <> 0 then
    begin
      SuppressibleMsgBox('Регистрация агента не удалась (код ' + IntToStr(Code) + ').' + #13#10#13#10 +
        'Проверьте адрес сервера, токен (он одноразовый и ограничен по времени) и доступ к серверу с этого компьютера. ' +
        'Подробности: ' + ExpandConstant('{commonappdata}\Sentinel\Agent\setup-enroll.log') + #13#10#13#10 +
        'Файлы агента установлены — можно повторить регистрацию командой:' + #13#10 +
        '"' + ExpandConstant('{app}') + '\SentinelAgent.exe" enroll --server <адрес> --token <токен>',
        mbError, MB_OK, IDOK);
      Exit;
    end;
  end;

  WizardForm.StatusLabel.Caption := 'Установка и запуск службы SentinelAgent…';
  RunAgent('install', 'setup-service.log');
  // Служба уже была (обновление) — после остановки в PrepareToInstall запускаем снова.
  Exec(ExpandConstant('{sys}\sc.exe'), 'start SentinelAgent', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function NeedRestart(): Boolean;
begin
  Result := RestartAfterNetFx;
end;
