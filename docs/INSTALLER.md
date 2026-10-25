# Инсталлятор BrainstormBuddy — полное руководство (Inno Setup)

Справка на будущее: как устроен, как пересобрать, как протестировать, какие
грабли и как их обошли. Инсталлятор — **Inno Setup 6**
(`packaging/inno/BrainstormBuddy.iss`).

---

## 1. Что это и почему Inno Setup

Обычный мастер установки, как у нормальных программ:
**приветствие → лицензия → «для всех / только для меня» → выбор папки →
папка в меню «Пуск» → доп. задачи (ярлык, автозапуск) → прогресс → финиш
с галочкой «Запустить»**. Язык мастера — русский (по умолчанию) или английский.

> **Почему ушли от Velopack.** Прежний `Setup.exe` (Velopack) молча
> распаковывался в `%LocalAppData%` и сразу запускался — без мастера, лицензии
> и выбора папки. Пользователи воспринимали это как вирус. Velopack удалён из
> проекта целиком (PackageReference `Velopack` + вызов `VelopackApp.Build().Run()`
> в `App.OnStartup`). Автообновлений сейчас нет (можно добавить позже отдельной
> проверкой релизов).

---

## 2. Требования и где что лежит

| Что | Где |
|---|---|
| Компилятор Inno | `%LocalAppData%\Programs\Inno Setup 6\ISCC.exe` (ставится `winget install JRSoftware.InnoSetup`) |
| Скрипт установщика | `packaging/inno/BrainstormBuddy.iss` (в git, UTF-8 **с BOM**) |
| Текст лицензии | `packaging/inno/LICENSE.txt` (в git, UTF-8 **с BOM**) |
| Иконка | `BrainstormBuddy/Resources/Icons/app.ico` |
| Публикация приложения | `publish/app-inno/` (gitignore) |
| Модели STT/OCR/диаризации | `publish/app-inno/models/` — обязательные GigaAM v3+v2 (`v3_e2e_ctc.int8.onnx`, `v3_e2e_ctc_vocab.txt`, `v2_ctc.onnx`, `labels.json`) и Nemotron GGUF (`Nemotron-3-Diarization.q8_0.gguf`); `publish/app-inno/nemo-speech/` — обязательный бандл движка диаризации (c-shim + реализация + ggml-сателлиты); `publish/app-inno/tessdata/` — обязательные модели OCR (rus/eng/osd); опциональные Whisper (`ggml-large-v3-turbo-q5_0.bin`, только Full) и e5 (`models/e5/`, только Full) |
| Готовый установщик | `installer-inno/BrainstormBuddy-Setup-{Lite,Full}.exe` + `SHA256SUMS.txt` (всё gitignore) |

---

## 3. Пошаговая пересборка (чеклист)

Канонический путь — `packaging/build-installer.sh` (git-bash): сам делает
self-contained publish приложения и MCP-хоста, копирует модели/руководства,
компилирует Lite всегда и Full (если есть `artifacts/whisper/ggml-large-v3-turbo-q5_0.bin`),
пишет `installer-inno/SHA256SUMS.txt`. Перед ISCC сносит устаревшие
`BrainstormBuddy-Setup-*.exe`, чтобы в суммы не попал exe прошлой сборки.

Ручной эквивалент:

```powershell
# 0) закрыть работающий BrainstormBuddy.exe (иначе publish упрётся в занятый файл)
Get-Process BrainstormBuddy* -EA SilentlyContinue | Stop-Process -Force

# 1) self-contained publish приложения и MCP-хоста (папка, НЕ single-file)
dotnet publish BrainstormBuddy\BrainstormBuddy.csproj -c Release -r win-x64 `
  --self-contained true -o publish\app-inno
dotnet publish BrainstormBuddy.McpHost\BrainstormBuddy.McpHost.csproj -c Release -r win-x64 `
  --self-contained true -o publish\app-inno

# 2) модели: GigaAM v3+v2, Nemotron GGUF и бандл nemo-speech обязательны
#    (nemo-speech кладёт packaging/fetch-nemo.sh; без него build-installer.sh
#    падает, а не собирает инсталлятор без диаризации); Whisper/e5 — только Full;
#    tessdata rus/eng/osd приезжает из BrainstormBuddy.csproj

# 3) собрать установщики: Lite без ключа, Full с /DIncludeWhisper
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" packaging\inno\BrainstormBuddy.iss
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DIncludeWhisper packaging\inno\BrainstormBuddy.iss
# → installer-inno\BrainstormBuddy-Setup-{Lite,Full}.exe
```

---

## 4. Устройство .iss (по секциям)

- **`[Setup]`** — метаданные и поведение:
  - `AppId={{B1F4A6E2-…}}` — **фиксированный GUID, не менять** между версиями,
    иначе апгрейд не найдёт прошлую установку.
  - `DefaultDirName={autopf}\BrainstormBuddy` — `{autopf}` = `Program Files`
    (для всех) **или** `%LocalAppData%\Programs` (только для меня).
  - `PrivilegesRequired=lowest` + `PrivilegesRequiredOverridesAllowed=dialog` —
    даёт выбор «для всех (нужен админ) / только для меня (без админа)».
  - `DisableWelcomePage=no`, `DisableDirPage=no`, `DisableProgramGroupPage=no` —
    **важно**: в `WizardStyle=modern` эти страницы по умолчанию скрыты; включаем явно.
  - `CloseApplications=yes` + `CloseApplicationsFilter=BrainstormBuddy.exe` —
    при апгрейде закрывает работающий экземпляр.
  - `Compression=lzma2/normal`, `SolidCompression=no` — сознательный выбор скорости
    сборки (многопоточно по всем ядрам) ценой ~10–15% размера exe; на финальный
    релиз можно вернуть `lzma2/max` + `solid`.
- **`[Languages]`** — `Russian.isl` (основной) + `Default.isl` (English).
- **`[CustomMessages]`** — двуязычные строки для типов/компонентов/задач.
- **`[Types]` / `[Components]`** — компонент `app` (`fixed`, ставится всегда):
  приложение + ОБЯЗАТЕЛЬНЫЕ GigaAM v3+v2 + nemo-speech/Nemotron + tessdata.
  Тип **«полная»** (`full`) есть в обеих сборках. Тип **«компактная»** (`compact`)
  и компонент `model` (Whisper + e5) существуют только в Full-сборке
  (`/DIncludeWhisper`) — там compact = full минус Whisper/e5. В Lite `compact`
  был бы дубликатом `full`, поэтому скрыт, а компонента `model` нет вовсе.
- **`[Files]`** — `publish\app-inno\*` в `{app}` с
  `Excludes: "models\*,tessdata\*,nemo-speech\*,config.json,*.pdb"`; GigaAM идёт
  отдельными строками под `Components: app` (обязательно), Whisper/e5 — под `model`.
- **`[Icons]`** — ярлык в «Пуск», деинсталлятор, ярлык на рабочем столе (по задаче).
- **`[Registry]`** — автозапуск: `HKCU\…\Run` (по задаче `autostart`,
  `uninsdeletevalue`).
- **`[Run]`** — запуск приложения после установки (галочка на финише).
- **`[Code]` `RemoveOldVelopack`** — миграция со старого Velopack-инсталлятора
  (см. ниже).

---

## 5. Куда что ставится и что НЕ трогается

- **Приложение** → `{app}` (`Program Files\BrainstormBuddy` или
  `%LocalAppData%\Programs\BrainstormBuddy`). Каталог только на чтение —
  приложение туда ничего не пишет (проверено).
- **Модели** → `{app}\models\{v3_e2e_ctc.int8.onnx, v3_e2e_ctc_vocab.txt}` (дефолтный движок v3) + `v2_ctc.onnx` (+`labels.json`).
- **Настройки/логи/история** → `%APPDATA%\BrainstormBuddy` (Roaming). Установка и
  удаление их **не трогают** — конфиг переживает переустановку.
- **`config.json` НЕ кладётся в `{app}`** — приложение читает конфиг только из
  `%APPDATA%`; dev-версия конфига содержала лишний LAN-адрес и старую схему.

**Приоритет поиска модели** (`GigaamV3ModelResolver`):
`config.SttModelPath` → `%APPDATA%\BrainstormBuddy\models` → `{app}\models`
(вариант из установщика) → dev `artifacts/`. GigaAM v3+v2 нельзя докачать из
приложения — поэтому установщик обязан класть их при ЛЮБОМ типе установки,
иначе свежая копия молча уходит в remote-STT. Whisper и e5, наоборот,
докачиваются из настроек и в установщике опциональны (Full).

---

## 6. Миграция со старого Velopack-инсталлятора

`[Code]`-процедура `RemoveOldVelopack` при установке:
1. ищет `%LocalAppData%\BrainstormBuddy\Update.exe`;
2. если есть — `Update.exe --uninstall --silent` (снимает ярлык, ключ ARP, файлы);
3. подчищает остаток каталога, ключ
   `HKCU\…\Uninstall\BrainstormBuddy` и ярлык в «Пуск».

**`%APPDATA%\BrainstormBuddy` (Roaming, настройки) НЕ трогается.** Всё обёрнуто в
`try/except` и запускается на шаге `ssInstall` — если Velopack не найден или
удаление не удалось, установка продолжается.

---

## 7. Чеклист тестирования (что проверять)

Тихая установка/удаление без UI (быстрая проверка механики):
```powershell
# установка «только для меня» (без UAC), полная (с моделью).
# ShowLanguageDialog=auto → диалог языка под /VERYSILENT пропускается сам;
# /LANG не нужен. /CURRENTUSER — ставим «только для меня» без диалога выбора scope.
Start-Process installer-inno\BrainstormBuddy-Setup-Full.exe -Wait `
  -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/CURRENTUSER"

# если приложение в данный момент запущено — принудительно закрыть,
# иначе CloseApplications=yes может ждать, пока tray-приложение завершится само:
#   ... + "/FORCECLOSEAPPLICATIONS"

# удаление
Start-Process "$env:LOCALAPPDATA\Programs\BrainstormBuddy\unins000.exe" -Wait `
  -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART"
```
Проверить после установки:
- [ ] `{app}\BrainstormBuddy.exe`, `{app}\models\v3_e2e_ctc.int8.onnx` (+`v3_e2e_ctc_vocab.txt`),
      `{app}\models\v2_ctc.onnx` есть; `config.json`
      и `*.pdb` — **нет**.
- [ ] `{app}\nemo-speech\nemo_speech_asr_c.dll`, `{app}\nemo-speech\nemo_speech_asr.dll`,
      `{app}\nemo-speech\ggml-vulkan.dll` и `{app}\models\Nemotron-3-Diarization.q8_0.gguf`
      есть (без них диаризация не загрузится).
- [ ] `{app}\tessdata\rus.traineddata`, `{app}\tessdata\eng.traineddata`,
      `{app}\tessdata\osd.traineddata` есть (OCR).
- [ ] старый `%LocalAppData%\BrainstormBuddy` удалён (миграция).
- [ ] ярлык в «Пуск» + запись в «Программах и компонентах» (`BrainstormBuddy 1.0.0`).
- [ ] запуск `{app}\BrainstormBuddy.exe` → в логе `Native STT ready (v3) … (provider=CPU)`
      и `STT engine: native3`; ошибки `Docker Desktop не найден` **нет**.
- [ ] `%APPDATA%\BrainstormBuddy\config.json` цел (размер не изменился).

После удаления:
- [ ] `{app}` удалён, ярлык и ключ ARP сняты, `%APPDATA%\…\config.json` **сохранён**.

> Мастер вживую (страницы, лицензия, выбор папки) — просто двойным кликом по
> `BrainstormBuddy-Setup.exe`. Скриншотилку рабочего стола автотест не имеет,
> поэтому визуал мастера смотрится вручную; механику покрывает тихая установка.

---

## 8. Грабли и решения (уроки этой работы)

- **UTF-8 без BOM → кракозябры.** Inno читает `.iss`/`LicenseFile` без BOM как ANSI.
  Кириллица ломается. **Решение:** сохранять `.iss` и `LICENSE.txt` в **UTF-8 с BOM**
  (после любого редактирования — перезаписать с BOM).
- **`WizardStyle=modern` прячет страницы.** Приветствие и выбор папки по умолчанию
  скрыты. **Решение:** `DisableWelcomePage=no`, `DisableDirPage=no`,
  `DisableProgramGroupPage=no`.
- **`config.json` из publish утекал в установщик** (в нём был LAN-адрес и старая
  схема, а приложению он не нужен — читает `%APPDATA%`). **Решение:** исключить в
  `[Files]` (`Excludes`). Заодно исключили `*.pdb`.
- **Дефолт `SttEngine=native3` (GigaAM v3) — модели обязательны в установщике.**
  GigaAM нельзя докачать из приложения (кнопка есть только у Whisper/e5). Поэтому
  GigaAM v3+v2 лежат в фиксированном компоненте `app` и ставятся при любом типе
  установки; историческая «compact без моделей» молча давала remote-STT — тип
  compact теперь существует только в Full и выкидывает лишь Whisper/e5. При
  отсутствии моделей `CreateSttEngine` откатывается на `remote` (см. комментарий
  в `AppConfig.SttEngine`).
- **Ложная ошибка «Docker Desktop не найден» при native.** `LocalSttService`
  автоподнимал Docker-мост независимо от движка. **Решение:** не автозапускать
  Docker-мост, если `SttEngine=native` (`App.OnStartup`).
- **Publish «поверх» без чистки безопасен** — dotnet не удаляет модель 900 МБ из
  output; переписываются только изменённые DLL. `Remove-Item` по пути проекта
  бывает заблокирован — чистка и не нужна.
- **`installer-inno/` и `installer-full/` — в `.gitignore`.** Исходники установщика
  (`.iss`, `LICENSE.txt`) лежат в **отслеживаемой** `packaging/inno/`, а не в
  игнорируемой `installer/`.
- **Кастомный `MsgBox` в `InitializeSetup` вешал тихую установку.** Голый `MsgBox`
  игнорирует `/SUPPRESSMSGBOXES` — под `/VERYSILENT` апгрейд ждал ответа вечно.
  **Решение:** все три промпта (upgrade/reinstall/downgrade) обёрнуты в
  `if not WizardSilent()`; тихие дефолты — обновление/переустановка = да,
  даунгрейд = нет (соответствует `MB_DEFBUTTON2`).

---

## 9. Медленный старт установщика (окно мастера через 1–2 минуты)

**Симптом:** двойной клик по `BrainstormBuddy-Setup-*.exe` (~0.5–1 ГБ) — и 1–2 минуты
не происходит **ничего** (ни окна, ни прогресса); потом мастер открывается и дальше всё
работает быстро. Появилось, когда exe вырос до сотен мегабайт. Комп при этом мощный.

**Причина — не Inno и не наш код, а антивирус.** Разбор по слоям:

1. **Microsoft Defender + SmartScreen.** Файл скачан из интернета → несёт Mark-of-the-Web
   (зона Internet). Он **не подписан** → при первом запуске SmartScreen проверяет репутацию,
   а Defender синхронно сканирует **весь** exe (включая распаковку LZMA2-потоков — а это ~1 ГБ
   сжатой модели) ДО того, как процессу вообще дадут выполниться. Именно тут уходят минуты,
   и время растёт с размером exe — что совпадает с наблюдением «сломалось, когда exe потолстел».
   Повторный запуск того же файла обычно быстрее (кэш вердикта Defender).
2. **SetupLdr (устройство Inno) — вне подозрений.** Загрузчик Inno при старте читает только
   заголовки и распаковывает во %TEMP% маленький setup-модуль (единицы МБ) — это секунды.
   Весь гигабайт распаковывается позже, на шаге копирования файлов, уже с прогресс-баром.
3. **Свой сплэш «подождите, идёт проверка» невозможен:** пока антивирус сканирует exe,
   не исполняется ещё **ни одна** наша инструкция — показывать сплэш просто некому.
   `InitializeSetup`/пре-мастерные окна Inno стартуют уже ПОСЛЕ этой паузы.

**Что сделано (дёшево, в 2.5.9):** `SetupMutex` в `[Setup]` — нетерпеливый юзер за эти
1–2 минуты кликает exe повторно; без мьютекса плодились параллельные мастера (и каждый
запускал своё АВ-сканирование, усугубляя тормоза). Теперь второй запуск вежливо сообщает,
что установка уже идёт.

**Что реально убирает задержку (варианты на будущее):**
- **Подпись кода платным сертификатом** (§10) — SmartScreen получает репутацию издателя и
  перестаёт держать процесс. Единственное решение, работающее у всех пользователей.
- Локально (только свои машины): снять MotW (Свойства файла → «Разблокировать») или добавить
  папку с инсталлятором в исключения Defender.
- В тексте раздачи/README честно писать: «Окно установки может появиться через 1–2 минуты —
  антивирус проверяет большой файл. Это нормально, не запускайте exe повторно.»

---

## 10. Подпись кода (SmartScreen)

Установщик **не подписан** → Windows SmartScreen покажет «неизвестный издатель».
Самоподписанный сертификат подписывает бинарь, но предупреждение **не убирает**
(Windows доверяет только публичным УЦ). Для «зелёной» установки нужен **платный**
code-signing сертификат. Заготовка — закомментированная строка `SignTool` в
`[Setup]`; подключается через `ISCC /S`.

---

## 11. Раздача (GitHub Releases)

Готовые `installer-inno\BrainstormBuddy-Setup-Lite.exe`,
`installer-inno\BrainstormBuddy-Setup-Full.exe` и `installer-inno\SHA256SUMS.txt`
публикуются как assets релиза на GitHub вместе с руководствами:
```powershell
gh release create v<версия> `
  installer-inno\BrainstormBuddy-Setup-Lite.exe `
  installer-inno\BrainstormBuddy-Setup-Full.exe `
  installer-inno\SHA256SUMS.txt
```

`SHA256SUMS.txt` содержит хэши установщиков, офлайн-моделей и файлов движков
(GigaAM, Whisper/e5, Nemotron GGUF, nemo-speech, tessdata) с путями от корня
репозитория и LF. Пользователи сверяют скачанные файлы через `Get-FileHash`.
Git Bash везёт BusyBox `sha256sum`, который при `-c` читает бинарники в text-mode
(CRLF/0x1A) и может выдать ложный FAILED даже на верных файлах. Проверка:
```powershell
Get-Content installer-inno/SHA256SUMS.txt | ForEach-Object {
  $hash, $path = $_ -split ' \*?', 2
  $sha = [Security.Cryptography.SHA256]::Create()
  $fs = [IO.File]::OpenRead((Resolve-Path -LiteralPath $path))
  try { $actual = ([BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-','').ToLower() }
  finally { $fs.Dispose(); $sha.Dispose() }
  '{0} {1}' -f $(if ($actual -eq $hash) { 'OK  ' } else { 'FAIL' }), $path
}
```
Сверка одного файла: `Get-FileHash -Algorithm SHA256`, либо (если командлет
недоступен в старом PowerShell) тот же .NET-пересчёт через `SHA256.Create()`.

---
## 12. Почему не MSIX

MSIX-песочница ломает стелс-фичи (`WDA_EXCLUDEFROMCAPTURE`, глобальные хоткеи).
Поэтому Inno Setup, не MSIX.

---

## 13. Автотест установки через UI-тестер (FlaUI-драйвер GUI)

Инсталлятор тестируется **не тихой установкой**, а прогоном реального GUI-мастера
через FlaUI — как живой пользователь кликает кнопки. Сценарий:
`BrainstormBuddy.UITests/Scenarios/InstallerScenario.cs`.

```bash
# установка → проверка файлов → сброс конфига → удаление (всё через GUI):
BrainstormBuddy.UITests.exe --install "installer-inno\BrainstormBuddy-Setup-Full.exe"
# только удаление уже установленного (для отладки):
BrainstormBuddy.UITests.exe --uninstall-only "C:\Users\<...>\AppData\Local\Programs\BrainstormBuddy"
```
Что делает: прокликивает **режим («только для меня»!) → язык → welcome → лицензия →
папка → сброс конфига → компоненты → меню Пуск → задачи → Install → Finish**, снимает
скриншот каждой страницы (Vision-аннотация, если локальный vision-сервер жив), затем проверяет файлы,
сброс конфига с бэкапом, и через GUI удаляет. Дев-конфиг (`%APPDATA%\...\config.json`)
бэкапится до и восстанавливается после — сброс не трогает реальные настройки.

Отчёт: `report_install.html` / `report_uninstall.html` рядом с запуском.

### Грабли, которые этот тест поймал (и как починены)
- **`THIRD-PARTY-NOTICES.txt` не попадал в установку.** Отдельная запись `Source:
  "..\..\THIRD-PARTY-NOTICES.txt"` в `[Files]` — ISCC **молча не паковал** файл по
  относительному пути (в бинаре инсталлятора имени файла не было вовсе, при этом сборка
  не падала). Фикс: `build-installer.sh` кладёт файл в `publish\app-inno\`, откуда его
  заметает общий свип `Source: "..\..\publish\app-inno\*"` (тот же, что ставит exe и dll).
- **Окно «Выбор режима установки»** (из `PrivilegesRequiredOverridesAllowed=dialog`)
  всплывает ДО мастера как `#32770`. Драйвер обязан жать **«Установить только для меня»**
  и НИКОГДА «для всех пользователей» — последнее это UAC-эскалация, после неё FlaUI
  (не поднятый) не управляет окном.
- **Деинсталлятор Inno** копирует себя в `%TEMP%` и перезапускается, **исходный процесс
  сразу выходит** → судить о завершении по `proc.HasExited` нельзя. Признак завершения
  удаления — **исчезновение exe**, а не выход процесса; финальный диалог дожимается.
