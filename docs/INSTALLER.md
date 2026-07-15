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
| Модель STT | `publish/app-inno/models/{v2_ctc.onnx, labels.json}` |
| Готовый установщик | `installer-inno/BrainstormBuddy-Setup.exe` (gitignore) |

---

## 3. Пошаговая пересборка (чеклист)

```powershell
# 0) закрыть работающий BrainstormBuddy.exe (иначе publish упрётся в занятый файл)
Get-Process BrainstormBuddy* -EA SilentlyContinue | Stop-Process -Force

# 1) self-contained publish (папка, НЕ single-file)
dotnet publish BrainstormBuddy\BrainstormBuddy.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false -o publish\app-inno

# 2) положить модель для «полной» установки (если её ещё нет в publish\app-inno\models)
robocopy publish\app\models publish\app-inno\models   # exit code 1 = успех (файлы скопированы)

# 3) собрать установщик (~5 мин: сжатие модели 900 МБ в solid-блок LZMA2/max)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" packaging\inno\BrainstormBuddy.iss
# → installer-inno\BrainstormBuddy-Setup.exe  (~465 МБ)
```

> Если меняли **код** — обязательно повторить publish (шаг 1) перед ISCC, иначе в
> установщик попадёт старый DLL. Publish можно делать «поверх» существующего
> `publish\app-inno` — модель 900 МБ не трогается (dotnet не чистит output), а
> изменённые DLL перезаписываются. Заново копировать модель не нужно.

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
  - `Compression=lzma2/max`, `SolidCompression=yes` — компактный результат.
- **`[Languages]`** — `Russian.isl` (основной) + `Default.isl` (English).
- **`[CustomMessages]`** — двуязычные строки для типов/компонентов/задач.
- **`[Types]` / `[Components]`** — **полная** (`full`, с моделью `model`) и
  **компактная** (`compact`, без модели). Компонент `app` — `fixed`.
- **`[Files]`** — `publish\app-inno\*` в `{app}` с
  `Excludes: "models\*,config.json,*.pdb"`; модель добавляется отдельными
  строками под компонент `model`.
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
- **Модель** → `{app}\models\v2_ctc.onnx` (+`labels.json`).
- **Настройки/логи/история** → `%APPDATA%\BrainstormBuddy` (Roaming). Установка и
  удаление их **не трогают** — конфиг переживает переустановку.
- **`config.json` НЕ кладётся в `{app}`** — приложение читает конфиг только из
  `%APPDATA%`; dev-версия конфига содержала лишний LAN-адрес и старую схему.

**Приоритет поиска модели** (`App.ResolveGigaamModel`):
`config.SttModelPath` → `%APPDATA%\models` → `{app}\models` (вариант из
установщика) → dev `artifacts/`. Поэтому на машине разработчика, где уже есть
модель в `%APPDATA%\models`, победит она — это нормально; у чистого пользователя
берётся модель из `{app}\models`.

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
# установка «только для меня» (без UAC), полная (с моделью)
Start-Process installer-inno\BrainstormBuddy-Setup.exe -Wait `
  -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/CURRENTUSER"

# удаление
Start-Process "$env:LOCALAPPDATA\Programs\BrainstormBuddy\unins000.exe" -Wait `
  -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART"
```
Проверить после установки:
- [ ] `{app}\BrainstormBuddy.exe`, `{app}\models\v2_ctc.onnx` есть; `config.json`
      и `*.pdb` — **нет**.
- [ ] старый `%LocalAppData%\BrainstormBuddy` удалён (миграция).
- [ ] ярлык в «Пуск» + запись в «Программах и компонентах» (`BrainstormBuddy 1.0.0`).
- [ ] запуск `{app}\BrainstormBuddy.exe` → в логе `Native STT ready … (provider=CPU)`
      и `STT engine: native`; ошибки `Docker Desktop не найден` **нет**.
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
- **Дефолт `SttEngine=remote` делал бы модель мёртвым грузом.** Полная установка
  кладёт модель, но приложение по умолчанию шло бы в Docker. **Решение:** дефолт
  `SttEngine=native` (при отсутствии модели `CreateSttEngine` сам откатывается на
  `remote`).
- **Ложная ошибка «Docker Desktop не найден» при native.** `LocalSttService`
  автоподнимал Docker-мост независимо от движка. **Решение:** не автозапускать
  Docker-мост, если `SttEngine=native` (`App.OnStartup`).
- **Publish «поверх» без чистки безопасен** — dotnet не удаляет модель 900 МБ из
  output; переписываются только изменённые DLL. `Remove-Item` по пути проекта
  бывает заблокирован — чистка и не нужна.
- **`installer-inno/` и `installer-full/` — в `.gitignore`.** Исходники установщика
  (`.iss`, `LICENSE.txt`) лежат в **отслеживаемой** `packaging/inno/`, а не в
  игнорируемой `installer/`.

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

## 11. Раздача (GitLab)

Готовый `installer-inno\BrainstormBuddy-Setup.exe` (~465 МБ) заливается в
**GitLab Generic Package Registry**:
```powershell
curl --header "PRIVATE-TOKEN: <token>" --upload-file installer-inno\BrainstormBuddy-Setup.exe `
  "https://gitlab.com/api/v4/projects/<id>/packages/generic/brainstormbuddy-setup/1.0.0/BrainstormBuddy-Setup.exe"
```
Репозиторий приватный → прямая ссылка требует токен в заголовке; проще скачивать
из веб-страницы **Packages** в залогиненном браузере. Токен — только в заголовке
запроса, **не** в исходниках.

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
скриншот каждой страницы (Vision-аннотация, если Ollama жив), затем проверяет файлы,
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
