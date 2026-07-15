; ============================================================================
;  BrainstormBuddy — установщик (Inno Setup 6)
;  Полноценный мастер: приветствие, лицензия, выбор папки, меню «Пуск»,
;  ярлыки, автозапуск, выбор «для всех / только для меня».
;  Компилировать: ISCC.exe BrainstormBuddy.iss
;  Источник приложения: ..\..\publish\app-inno  (self-contained publish + models\)
; ============================================================================

#define MyAppName "BrainstormBuddy"
#define MyAppVersion "2.5.10"
#define MyAppPublisher "BrainstormBuddy"
#define MyAppExeName "BrainstormBuddy.exe"
; Стабильный AppId — НЕ менять между версиями (иначе апгрейд не найдёт прошлую установку).
#define MyAppId "{{B1F4A6E2-7C3D-4E90-9A21-2D6C8F0B5A44}"
; Тот же GUID одинарными скобками — для чтения ключа удаления Inno ({GUID}_is1) в [Code].
#define MyAppGuid "{B1F4A6E2-7C3D-4E90-9A21-2D6C8F0B5A44}"
; Опциональный бандл ffmpeg (для webm/mkv). Компонент появляется в мастере ТОЛЬКО если
; положить ffmpeg.exe в packaging\inno\ffmpeg\ перед сборкой. Иначе — докачка в приложении.
#define HaveFfmpeg FileExists(AddBackslash(SourcePath) + "ffmpeg\ffmpeg.exe")
; Полная «Full»-сборка (со встроенной офлайн-моделью Whisper) собирается ключом ISCC /DIncludeWhisper.
; Без ключа получается «Lite» (только GigaAM, Whisper докачивается в приложении).
#ifdef IncludeWhisper
  #define SetupVariant "Full"
#else
  #define SetupVariant "Lite"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=BrainstormBuddy — невидимый ассистент для живых созвонов

; Папка по умолчанию: {autopf} = Program Files (для всех) ИЛИ
; %LocalAppData%\Programs (только для меня) — зависит от выбора пользователя.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; При обновлении переиспользуем прежнюю папку и группу — не спрашиваем заново.
UsePreviousAppDir=yes
UsePreviousGroup=yes

; Пользователь ДОЛЖЕН видеть эти страницы — не отключаем.
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=no
LicenseFile=LICENSE.txt

; Выбор «для всех пользователей (нужен админ) / только для меня (без админа)».
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Результат сборки
OutputDir=..\..\installer-inno
OutputBaseFilename=BrainstormBuddy-Setup-{#SetupVariant}
SetupIconFile=..\..\BrainstormBuddy\Resources\Icons\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}

WizardStyle=modern
ShowLanguageDialog=yes
; Быстрая сборка: lzma2/normal вместо max + без solid → жмётся в несколько потоков (все ядра).
; Инсталлятор чуть больше (~+10-15%), но сборка в разы быстрее. Для финального релиза
; при желании вернуть max + solid ради минимального размера.
Compression=lzma2/normal
SolidCompression=no
LZMANumBlockThreads=4
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Закрыть работающий BrainstormBuddy.exe при обновлении (без принудительного ребута).
CloseApplications=yes
CloseApplicationsFilter=BrainstormBuddy.exe
RestartApplications=no

; Окно мастера у ~1 ГБ exe появляется через 1-2 мин (антивирус сканирует неподписанный файл
; при первом запуске — см. docs/INSTALLER.md «Медленный старт установщика»). Нетерпеливый юзер
; за это время кликает exe ещё раз — без мьютекса плодились параллельные мастера.
SetupMutex={#MyAppName}_SetupMutex

; --- Подпись кода (опционально) --------------------------------------------
; SmartScreen перестанет пугать «неизвестный издатель» только с ПЛАТНЫМ
; сертификатом публичного УЦ. Самоподписанный не убирает предупреждение.
; SignTool=signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f
; ---------------------------------------------------------------------------

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.FullType=Полная установка (офлайн-модель распознавания речи включена, ~1 ГБ)
english.FullType=Full installation (offline speech model included, ~1 GB)
russian.CompactType=Компактная установка (модель скачается при первом запуске, ~80 МБ)
english.CompactType=Compact installation (model downloads on first run, ~80 MB)
russian.CustomType=Выборочная установка
english.CustomType=Custom installation
russian.AppComp=Приложение BrainstormBuddy
english.AppComp=BrainstormBuddy application
#ifdef IncludeWhisper
russian.ModelComp=Офлайн-модели распознавания: GigaAM + Whisper (~1.5 ГБ)
english.ModelComp=Offline speech models: GigaAM + Whisper (~1.5 GB)
#else
russian.ModelComp=Офлайн-модель распознавания речи GigaAM (~1 ГБ)
english.ModelComp=Offline speech recognition model GigaAM (~1 GB)
#endif
russian.FfmpegComp=ffmpeg — поддержка webm/mkv/opus (иначе докачается в приложении)
english.FfmpegComp=ffmpeg — webm/mkv/opus support (otherwise downloaded in-app)
russian.AutostartTask=Запускать BrainstormBuddy при входе в Windows
english.AutostartTask=Launch BrainstormBuddy at Windows startup
russian.AutostartGroup=Автозапуск:
english.AutostartGroup=Startup options:
russian.RemovingOld=Удаляю предыдущую версию (Velopack)...
english.RemovingOld=Removing previous version (Velopack)...
russian.UpgradeConfirm=Установлена версия %1. Обновить до версии %2?
english.UpgradeConfirm=Version %1 is installed. Update to version %2?
russian.ReinstallConfirm=Версия %1 уже установлена. Переустановить (восстановить файлы)?
english.ReinstallConfirm=Version %1 is already installed. Reinstall (repair files)?
russian.DowngradeConfirm=Установлена более новая версия %1. Установить более старую %2 поверх?
english.DowngradeConfirm=A newer version %1 is installed. Install the older %2 over it?

[Types]
Name: "full";    Description: "{cm:FullType}"
Name: "compact"; Description: "{cm:CompactType}"
Name: "custom";  Description: "{cm:CustomType}"; Flags: iscustom

[Components]
Name: "app";   Description: "{cm:AppComp}";   Types: full compact custom; Flags: fixed
Name: "model"; Description: "{cm:ModelComp}"; Types: full
#if HaveFfmpeg
Name: "ffmpeg"; Description: "{cm:FfmpegComp}"; Types: full
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart";   Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"; Flags: unchecked

[Files]
; Приложение — всё, КРОМЕ каталога models (отдельный компонент), dev-конфига и .pdb.
; config.json НЕ кладём: приложение читает конфиг из %APPDATA%, а dev-версия содержит
; лишний LAN-адрес и устаревшую схему. Чистый первый запуск сгенерит дефолты сам.
; Сюда попадает и THIRD-PARTY-NOTICES.txt — build-installer.sh кладёт его в publish\app-inno\
; (обязательство MIT — приложить копирайты). Отдельная запись Source не использовалась:
; при относительном пути ISCC молча не паковал файл, поэтому кладём его в общий свип.
Source: "..\..\publish\app-inno\*"; DestDir: "{app}"; Excludes: "models\*,config.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
; Офлайн-модель — только если выбран компонент "model" (тип "full").
Source: "..\..\publish\app-inno\models\v2_ctc.onnx"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model
Source: "..\..\publish\app-inno\models\labels.json"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model
#ifdef IncludeWhisper
; Whisper large-v3-turbo — только в полной «Full»-сборке (офлайн-транскрибация файлов + английский).
Source: "..\..\publish\app-inno\models\ggml-large-v3-turbo-q5_0.bin"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model
#endif
#if HaveFfmpeg
; Опциональный бандл ffmpeg (для webm/mkv), если положен в packaging\inno\ffmpeg\.
Source: "ffmpeg\ffmpeg.exe"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion; Components: ffmpeg
#endif

[Dirs]
; Папка экспорта транскриптов/протоколов в «Документах» пользователя (у каждого своя).
; uninsneveruninstall — не удаляем при деинсталляции (там пользовательские файлы).
Name: "{userdocs}\{#MyAppName}"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Автозапуск (по галочке) — ключ Run текущего пользователя, чистится при удалении.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  PrevVersion: String;
  UpgradeMode: Boolean;   { True = ставим поверх существующей установки (апгрейд/переустановка) }
  ConfigResetPage: TInputOptionWizardPage;   { выбор: сбросить настройки или оставить }
  OldConfigPath: String;                      { %APPDATA%\BrainstormBuddy\config.json }

{ Отделяет первый числовой компонент версии "1.2.3" (модифицирует S). }
function VerPart(var S: String): Integer;
var P: Integer;
begin
  P := Pos('.', S);
  if P = 0 then
  begin
    Result := StrToIntDef(S, 0);
    S := '';
  end
  else
  begin
    Result := StrToIntDef(Copy(S, 1, P - 1), 0);
    Delete(S, 1, P);
  end;
end;

{ Сравнение версий: >0 если A новее B, 0 равны, <0 A старее. }
function CompareVer(A, B: String): Integer;
var NA, NB: Integer;
begin
  Result := 0;
  while (A <> '') or (B <> '') do
  begin
    NA := VerPart(A);
    NB := VerPart(B);
    if NA > NB then begin Result := 1; Exit; end;
    if NA < NB then begin Result := -1; Exit; end;
  end;
end;

{ Читает DisplayVersion уже установленной версии (ключ Inno GUID_is1) — для всех и для юзера. }
function GetInstalledVersion(): String;
var V: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppGuid}_is1', 'DisplayVersion', V) then
    Result := V
  else if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppGuid}_is1', 'DisplayVersion', V) then
    Result := V;
end;

{ Умное поведение при повторном запуске: обновление / переустановка / попытка даунгрейда. }
function InitializeSetup(): Boolean;
var Cmp: Integer;
begin
  Result := True;
  UpgradeMode := False;
  PrevVersion := GetInstalledVersion();
  if PrevVersion = '' then Exit;   { чистая установка — обычный мастер }

  Cmp := CompareVer('{#MyAppVersion}', PrevVersion);
  if Cmp > 0 then
  begin
    { Новее установленной — предлагаем обновить, дальше страницы папки/группы пропускаем }
    UpgradeMode := True;
    if MsgBox(FmtMessage(CustomMessage('UpgradeConfirm'), [PrevVersion, '{#MyAppVersion}']), mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end
  else if Cmp = 0 then
  begin
    UpgradeMode := True;
    if MsgBox(FmtMessage(CustomMessage('ReinstallConfirm'), ['{#MyAppVersion}']), mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end
  else
  begin
    { Установлена более новая версия — по умолчанию НЕ ставим старую поверх }
    if MsgBox(FmtMessage(CustomMessage('DowngradeConfirm'), [PrevVersion, '{#MyAppVersion}']), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      UpgradeMode := True
    else
      Result := False;
  end;
end;

{ Создаём страницу выбора судьбы старого конфига (показывается только если он есть). }
procedure InitializeWizard();
begin
  OldConfigPath := ExpandConstant('{userappdata}\{#MyAppName}\config.json');
  ConfigResetPage := CreateInputOptionPage(wpSelectDir,
    'Настройки предыдущей версии',
    'Обнаружены настройки от прошлой установки.',
    'Старый конфиг может конфликтовать с новой версией (устаревшие адреса серверов, дубли пресетов). Что сделать?',
    True, False);
  ConfigResetPage.Add('Сбросить к новым настройкам (рекомендуется). Старый конфиг сохранится в резервную копию в «Документах».');
  ConfigResetPage.Add('Оставить мои старые настройки (режим совместимости).');
  ConfigResetPage.SelectedValueIndex := 0;
end;

{ На апгрейде/переустановке пропускаем избыточные страницы — папка и группа уже известны.
  Страницу сброса конфига показываем только если старый конфиг реально существует. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := UpgradeMode and ((PageID = wpWelcome) or (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup));
  if PageID = ConfigResetPage.ID then
    Result := not FileExists(OldConfigPath);
end;

{ Удаляет прежнюю установку через Velopack, чтобы не было двух копий.
  Velopack ставился в %LocalAppData%\BrainstormBuddy (Update.exe + current\ + packages\),
  плюс ярлык в «Пуск» и ключ в «Программы и компоненты» (HKCU\...\Uninstall\BrainstormBuddy).
  ВАЖНО: НЕ трогаем %APPDATA%\BrainstormBuddy (Roaming) — там настройки и логи пользователя.
  Best-effort: любые ошибки глушим, установку не блокируем. }
procedure RemoveOldVelopack;
var
  VeloDir, VeloUpdate: String;
  ResultCode: Integer;
begin
  VeloDir := ExpandConstant('{localappdata}\BrainstormBuddy');

  { Страховка: если бы Inno ставился в тот же каталог — не запускаем удаление,
    только чистим устаревший ключ реестра. }
  if CompareText(RemoveBackslash(VeloDir), RemoveBackslash(ExpandConstant('{app}'))) = 0 then
  begin
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\BrainstormBuddy');
    exit;
  end;

  VeloUpdate := VeloDir + '\Update.exe';
  if FileExists(VeloUpdate) then
  begin
    WizardForm.StatusLabel.Caption := ExpandConstant('{cm:RemovingOld}');
    Log('Найдена прежняя установка Velopack: ' + VeloUpdate + ' — удаляю.');
    { Штатное удаление: снимает ярлык, ключ ARP и файлы. }
    Exec(VeloUpdate, '--uninstall --silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  { Подчистка остатков (best effort). }
  if DirExists(VeloDir) then
    DelTree(VeloDir, True, True, True);
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\BrainstormBuddy');
  DeleteFile(ExpandConstant('{userprograms}\BrainstormBuddy.lnk'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExportDir, BackupDir, BackupPath: String;
begin
  if CurStep = ssInstall then
  begin
    try
      RemoveOldVelopack;
    except
      Log('RemoveOldVelopack failed (ignored): ' + GetExceptionMessage);
    end;

    { Сброс настроек по выбору пользователя: старый config.json → бэкап в «Документы»,
      оригинал удаляем, чтобы приложение создало свежие дефолты. Резюме/пресеты внутри
      бэкапа сохранены — восстановимо. При «совместимости» конфиг не трогаем. }
    if FileExists(OldConfigPath) and (ConfigResetPage.SelectedValueIndex = 0) then
    begin
      BackupDir := ExpandConstant('{userdocs}\{#MyAppName}');
      ForceDirectories(BackupDir);
      BackupPath := BackupDir + '\config.backup-' + GetDateTimeString('yyyymmdd_hhnnss', #0, #0) + '.json';
      if CopyFile(OldConfigPath, BackupPath, False) then
      begin
        if DeleteFile(OldConfigPath) then
          Log('Config reset: бэкап в ' + BackupPath + ', оригинал удалён')
        else
          Log('Config reset: ВНИМАНИЕ — оригинал не удалился (залочен?), приложение будет читать старый: ' + OldConfigPath);
      end
      else
        Log('Config reset: не удалось создать бэкап, оставляю старый конфиг');
    end;
  end;

  { Гарантируем и проверяем папку экспорта в «Документах» пользователя. }
  if CurStep = ssPostInstall then
  begin
    ExportDir := ExpandConstant('{userdocs}\{#MyAppName}');
    if not DirExists(ExportDir) then
      ForceDirectories(ExportDir);
    if DirExists(ExportDir) then
      Log('Папка экспорта готова: ' + ExportDir)
    else
      Log('ВНИМАНИЕ: не удалось создать папку экспорта: ' + ExportDir);
  end;
end;
