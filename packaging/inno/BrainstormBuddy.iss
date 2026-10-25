; ============================================================================
;  BrainstormBuddy — установщик (Inno Setup 6)
;  Полноценный мастер: приветствие, лицензия, выбор папки, меню «Пуск»,
;  ярлыки, автозапуск, выбор «для всех / только для меня».
;  Компилировать: ISCC.exe BrainstormBuddy.iss
;  Источник приложения: ..\..\publish\app-inno  (self-contained publish + models\)
; ============================================================================

#define MyAppName "BrainstormBuddy"
#define MyAppVersion "2.39.2"
#define MyAppPublisher "BrainstormBuddy"
#define MyAppExeName "BrainstormBuddy.exe"
; MCP-хост — отдельный stdio-процесс, который запускают внешние ИИ-агенты
; (Codex/Claude) из подключения к Buddy MCP. Он держит свой .dll и не даёт
; установщику заменить файлы — см. EnsureProcessesClosed в [Code].
#define MyAppHostExeName "BrainstormBuddy.McpHost.exe"
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
; yes (не auto): мануал и UX ждут явный выбор языка инсталлятора. На silent-установках
; диалог не показывается в любом случае — /SILENT подавляет его сам, /LANG задаёт язык.
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
; MCP-хост в фильтр добавлен: Restart Manager умеет закрывать только приложения
; с окнами, а хост — консольный процесс, поэтому его закрываем сами (см. [Code]).
CloseApplications=yes
CloseApplicationsFilter=BrainstormBuddy.exe;BrainstormBuddy.McpHost.exe
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
#ifdef IncludeWhisper
russian.FullType=Полная установка (GigaAM + офлайн-Whisper и e5-эмбеддинги, ~1.7 ГБ)
english.FullType=Full installation (GigaAM + offline Whisper and e5 embeddings, ~1.7 GB)
russian.CompactType=Компактная установка (без Whisper/e5 — докачаются из настроек, ~1.2 ГБ)
english.CompactType=Compact installation (without Whisper/e5 — downloaded from settings, ~1.2 GB)
russian.ModelComp=Доп. офлайн-модели: Whisper (англ. речь) + e5-эмбеддинги памяти (~1 ГБ)
english.ModelComp=Additional offline models: Whisper (English speech) + e5 memory embeddings (~1 GB)
#else
russian.FullType=Установка BrainstormBuddy (GigaAM v3+v2 включены, ~1.2 ГБ)
english.FullType=Install BrainstormBuddy (GigaAM v3+v2 included, ~1.2 GB)
#endif
russian.CustomType=Выборочная установка
english.CustomType=Custom installation
russian.AppComp=Приложение BrainstormBuddy (включая модели GigaAM v3+v2)
english.AppComp=BrainstormBuddy application (including GigaAM v3+v2 models)
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
russian.CloseAppPrompt=BrainstormBuddy сейчас запущен и держит свои файлы — установщик не сможет их заменить. Закрыть приложение и продолжить установку? Текущая встреча будет прервана.
english.CloseAppPrompt=BrainstormBuddy is running and holds its files, so the installer cannot replace them. Close the application and continue the installation? The current call will be interrupted.
russian.CloseAppFailed=Не удалось закрыть BrainstormBuddy. Закройте приложение вручную и запустите установщик снова.
english.CloseAppFailed=Could not close BrainstormBuddy. Close the application manually and run the installer again.
russian.CloseMcpHostPrompt=ИИ-агент (Codex/Claude) запустил MCP-хост BrainstormBuddy, и тот держит файл BrainstormBuddy.McpHost.dll — установщик не сможет его заменить. Закрыть MCP-хост и продолжить? Если агент активен, закройте и его: агент сразу запустит хост заново.
english.CloseMcpHostPrompt=An AI agent (Codex/Claude) started the BrainstormBuddy MCP host, which holds BrainstormBuddy.McpHost.dll, so the installer cannot replace it. Close the MCP host and continue? If the agent is still running, close it too: the agent will start a new host right away.
russian.CloseMcpHostFailed=Не удалось закрыть MCP-хост BrainstormBuddy. Закройте ИИ-агент (Codex/Claude) и запустите установщик снова.
english.CloseMcpHostFailed=Could not close the BrainstormBuddy MCP host. Close the AI agent (Codex/Claude) and run the installer again.

[Types]
Name: "full";    Description: "{cm:FullType}"
#ifdef IncludeWhisper
; "compact" существует только в Full-сборке: там он выкидывает НЕобязательные
; Whisper/e5 из компонента model. В Lite (без IncludeWhisper) он был бы бессмысленным
; дубликатом "full", поэтому скрыт.
Name: "compact"; Description: "{cm:CompactType}"
#endif
Name: "custom";  Description: "{cm:CustomType}"; Flags: iscustom

[Components]
; "app" — фиксированная база: само приложение + ОБЯЗАТЕЛЬНЫЕ модели GigaAM v3+v2
; (дефолтный SttEngine=native3; качать их из настроек нельзя). Никакой тип установки
; не должен отдавать пользователю приложение без них. Тип "compact" объявлен только
; под IncludeWhisper — в Lite его имя в Types: нельзя упоминать (ISCC ошибка).
#ifdef IncludeWhisper
Name: "app";   Description: "{cm:AppComp}";   Types: full compact custom; Flags: fixed
Name: "model"; Description: "{cm:ModelComp}"; Types: full
#else
Name: "app";   Description: "{cm:AppComp}";   Types: full custom; Flags: fixed
#endif
#if HaveFfmpeg
Name: "ffmpeg"; Description: "{cm:FfmpegComp}"; Types: full
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart";   Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"; Flags: unchecked

[Files]
; Приложение — всё, КРОМЕ models\, tessdata\ и nemo-speech\ (отдельные Source ниже),
; dev-конфига и .pdb. config.json НЕ кладём: приложение читает конфиг из %APPDATA%,
; а dev-версия содержит лишний LAN-адрес и устаревшую схему. Чистый первый запуск
; сгенерит дефолты сам. Сюда попадает и THIRD-PARTY-NOTICES.txt — build-installer.sh
; кладёт его в publish\app-inno\ (обязательство MIT — приложить копирайты). Отдельная
; запись Source не использовалась: при относительном пути ISCC молча не паковал файл.
Source: "..\..\publish\app-inno\*"; DestDir: "{app}"; Excludes: "models\*,tessdata\*,nemo-speech\*,config.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
; Офлайн-модели GigaAM — в ФИКСИРОВАННОМ компоненте "app": ставятся при ЛЮБОМ типе
; установки (включая compact/custom без "model"). Это обязательные файлы:
; дефолтный движок — v3 (Audio.SttEngine="native3"), а скачать их из приложения
; нельзя — без них свежая установка молча уходила бы в remote-STT.
Source: "..\..\publish\app-inno\models\v2_ctc.onnx"; DestDir: "{app}\models"; Flags: ignoreversion; Components: app
Source: "..\..\publish\app-inno\models\labels.json"; DestDir: "{app}\models"; Flags: ignoreversion; Components: app
; GigaAM v3 (v3_e2e_ctc.int8.onnx + vocab, ~215 МБ) — без skipifsourcedoesntexist:
; отсутствие должно ронять сборку ещё в build-installer.sh.
Source: "..\..\publish\app-inno\models\v3_e2e_ctc.int8.onnx"; DestDir: "{app}\models"; Flags: ignoreversion; Components: app
Source: "..\..\publish\app-inno\models\v3_e2e_ctc_vocab.txt"; DestDir: "{app}\models"; Flags: ignoreversion; Components: app
; Движок диаризации Nemotron (Sortformer через nemo_speech_asr_c.dll) — ОБЯЗАТЕЛЬНЫЙ
; компонент: бандл nemo-speech vulkan (покрывает cpu) в {app}\nemo-speech (~56 МБ),
; GGUF-модель (~82 МБ) в {app}\models. Раскладка — packaging/fetch-nemo.sh →
; build-installer.sh (там жёсткий exit при отсутствии, до ISCC). Без
; skipifsourcedoesntexist: если файла нет — ISCC падает, а не молча выпускает
; инсталлятор без диаризации (TASK §175).
Source: "..\..\publish\app-inno\nemo-speech\*"; DestDir: "{app}\nemo-speech"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: app
Source: "..\..\publish\app-inno\models\Nemotron-3-Diarization.q8_0.gguf"; DestDir: "{app}\models"; Flags: ignoreversion; Components: app
; Модели Tesseract (rus+eng+osd) для вкладки «Экран OCR» — всегда, отдельным каталогом:
; TesseractRecognizer ищет их только в {app}\tessdata. Из свипа исключены (см. Excludes),
; чтобы зависимость OCR была видна в инсталляторе явно, а не «где-то в свипе».
Source: "..\..\publish\app-inno\tessdata\*"; DestDir: "{app}\tessdata"; Flags: ignoreversion; Components: app
#ifdef IncludeWhisper
; Whisper large-v3-turbo — только в полной «Full»-сборке (офлайн-транскрибация файлов + английский).
Source: "..\..\publish\app-inno\models\ggml-large-v3-turbo-q5_0.bin"; DestDir: "{app}\models"; Flags: ignoreversion; Components: model
#endif
#ifdef IncludeWhisper
; E5-эмбеддинги памяти (multilingual-e5-small, FP32 ~470 МБ) — только в Full
; («память из коробки», с 2.16.0). Бандл опционален: если build-installer.sh не
; положил artifacts\e5 → записи пропускаются, Lite-сценарий «докачать по кнопке»
; остаётся рабочим (E5ModelDownloader в настройках).
Source: "..\..\publish\app-inno\models\e5\model.onnx"; DestDir: "{app}\models\e5"; Flags: ignoreversion skipifsourcedoesntexist; Components: model
Source: "..\..\publish\app-inno\models\e5\sentencepiece.bpe.model"; DestDir: "{app}\models\e5"; Flags: ignoreversion skipifsourcedoesntexist; Components: model
Source: "..\..\publish\app-inno\models\e5\config.json"; DestDir: "{app}\models\e5"; Flags: ignoreversion skipifsourcedoesntexist; Components: model
Source: "..\..\publish\app-inno\models\e5\tokenizer_config.json"; DestDir: "{app}\models\e5"; Flags: ignoreversion skipifsourcedoesntexist; Components: model
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

{ --- Процессы, удерживающие файлы установки -------------------------------- }
{ Restart Manager (CloseApplications) умеет закрывать только приложения с окнами.
  MCP-хост BrainstormBuddy.McpHost.exe запускают внешние ИИ-агенты (Codex/Claude)
  из подключения к Buddy MCP — это консольный процесс без окон, RM его не трогает,
  и файл BrainstormBuddy.McpHost.dll остаётся залоченным: установка падает с
  «DeleteFile ... Отказано в доступе». Поэтому оба процесса ловим сами. }

{ Есть ли процесс с таким именем (точное имя exe). }
function ProcessRunning(const ExeName: String): Boolean;
var
  ResultCode: Integer;
  ListFile: String;
  CmdLine: String;
  ListText: AnsiString;
  SearchText: String;
begin
  Result := False;
  ListFile := ExpandConstant('{tmp}\bb-tasklist.csv');
  DeleteFile(ListFile);
  { tasklist пишет CSV в файл: так вывод читается без перехвата stdout. }
  CmdLine := '/c tasklist /NH /FO CSV > "' + ListFile + '" 2>nul';
  if not Exec(ExpandConstant('{cmd}'), CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if not LoadStringFromFile(ListFile, ListText) then
    Exit;
  DeleteFile(ListFile);
  { Имя образа в CSV заключено в кавычки - так "BrainstormBuddy.exe" не совпадёт
    с "BrainstormBuddy.McpHost.exe". }
  SearchText := ListText;
  Result := Pos('"' + Uppercase(ExeName) + '"', Uppercase(SearchText)) > 0;
end;

{ Останавливает процесс: сначала мягко (WM_CLOSE), при неудаче — принудительно. }
procedure StopProcess(const ExeName: String; Force: Boolean);
var
  ResultCode: Integer;
  CmdLine: String;
begin
  CmdLine := '/c taskkill /IM "' + ExeName + '"';
  if Force then
    CmdLine := CmdLine + ' /F';
  Exec(ExpandConstant('{cmd}'), CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Ждёт исчезновения процесса (не дольше указанного числа секунд). }
function WaitProcessExit(const ExeName: String; Seconds: Integer): Boolean;
var I: Integer;
begin
  for I := 1 to Seconds * 2 do
  begin
    if not ProcessRunning(ExeName) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := not ProcessRunning(ExeName);
end;

{ Закрывает приложение BrainstormBuddy: мягко, затем принудительно. }
procedure CloseAppProcess;
begin
  StopProcess('{#MyAppExeName}', False);
  if not WaitProcessExit('{#MyAppExeName}', 12) then
  begin
    StopProcess('{#MyAppExeName}', True);
    WaitProcessExit('{#MyAppExeName}', 5);
  end;
end;

{ Закрывает процессы, удерживающие файлы установки. Ask = True -> спрашиваем
  пользователя (в тихой установке вопрос не показывается и процессы закрываются
  молча). False -> установка прерывается, если процесс закрыть не удалось. }
function EnsureProcessesClosed(Ask: Boolean): Boolean;
var
  HadApp, HadHost: Boolean;
begin
  Result := True;
  HadApp := ProcessRunning('{#MyAppExeName}');
  HadHost := ProcessRunning('{#MyAppHostExeName}');
  if not (HadApp or HadHost) then Exit;

  if HadApp then
  begin
    if Ask and (MsgBox(CustomMessage('CloseAppPrompt'), mbConfirmation, MB_YESNO) = IDNO) then
    begin
      Result := False;
      Exit;
    end;
    Log('Закрываю работающий ' + '{#MyAppExeName}');
    CloseAppProcess;
    if ProcessRunning('{#MyAppExeName}') then
    begin
      MsgBox(CustomMessage('CloseAppFailed'), mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if HadHost then
  begin
    if Ask and (MsgBox(CustomMessage('CloseMcpHostPrompt'), mbConfirmation, MB_YESNO) = IDNO) then
    begin
      Result := False;
      Exit;
    end;
    { Хост — вспомогательный stdio-процесс клиента: в нём нет состояния, ИИ-агент
      поднимет новый при следующем обращении. }
    Log('Закрываю работающий ' + '{#MyAppHostExeName}');
    StopProcess('{#MyAppHostExeName}', True);
    WaitProcessExit('{#MyAppHostExeName}', 10);
    if ProcessRunning('{#MyAppHostExeName}') then
    begin
      MsgBox(CustomMessage('CloseMcpHostFailed'), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

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
  { Процессы, удерживающие файлы установки (приложение и MCP-хост ИИ-агента),
    закрываем ДО мастера: иначе копирование падает с «Отказано в доступе».
    В тихой установке спрашивать нечем — закрываем молча. }
  if not EnsureProcessesClosed(not WizardSilent()) then
  begin
    Result := False;
    Exit;
  end;
  PrevVersion := GetInstalledVersion();
  if PrevVersion = '' then Exit;   { чистая установка — обычный мастер }

  Cmp := CompareVer('{#MyAppVersion}', PrevVersion);
  { Под /SILENT и /VERYSILENT MsgBox НЕ показываем — голый MsgBox игнорирует
    /SUPPRESSMSGBOXES и вешал бы автодеплой навсегда. Тихие дефолты:
    обновление и переустановка — да, даунгрейд — нет (как MB_DEFBUTTON2). }
  if Cmp > 0 then
  begin
    { Новее установленной — предлагаем обновить, дальше страницы папки/группы пропускаем }
    UpgradeMode := True;
    if not WizardSilent() and
       (MsgBox(FmtMessage(CustomMessage('UpgradeConfirm'), [PrevVersion, '{#MyAppVersion}']), mbConfirmation, MB_YESNO) = IDNO) then
      Result := False;
  end
  else if Cmp = 0 then
  begin
    UpgradeMode := True;
    if not WizardSilent() and
       (MsgBox(FmtMessage(CustomMessage('ReinstallConfirm'), ['{#MyAppVersion}']), mbConfirmation, MB_YESNO) = IDNO) then
      Result := False;
  end
  else
  begin
    { Установлена более новая версия — по умолчанию НЕ ставим старую поверх }
    if not WizardSilent() and
       (MsgBox(FmtMessage(CustomMessage('DowngradeConfirm'), [PrevVersion, '{#MyAppVersion}']), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
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
    { Повторная проверка перед копированием: ИИ-агент мог поднять MCP-хост заново,
      пока пользователь шёл по мастеру. Молча закрываем — согласие уже получено. }
    if not EnsureProcessesClosed(False) then
      Log('ВНИМАНИЕ: не удалось закрыть процесс, удерживающий файлы установки');

    try
      RemoveOldVelopack;
    except
      Log('RemoveOldVelopack failed (ignored): ' + GetExceptionMessage);
    end;

    { Сброс настроек по выбору пользователя: старый config.json → бэкап в «Документы»,
      оригинал удаляем, чтобы приложение создало свежие дефолты. Резюме/пресеты внутри
      бэкапа сохранены — восстановимо. При «совместимости» конфиг не трогаем.
      Под /SILENT страница выбора не показывалась → конфиг юзера НЕ трогаем. }
    if not WizardSilent() and FileExists(OldConfigPath) and (ConfigResetPage.SelectedValueIndex = 0) then
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

