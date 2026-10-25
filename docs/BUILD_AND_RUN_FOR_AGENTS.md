# BrainstormBuddy — сборка и запуск (для ИИ‑агентов и разработчиков)

Практическая инструкция «с нуля». Проверено чистым клоном ветки `main`
(коммит-слияние `1ceac4c`): **клонируется, собирается и запускается**.

---

## 1. Требования (обязательно)

| Компонент | Версия / примечание |
|---|---|
| **OS** | **Только Windows** (10 2004+ / 11). Приложение на **WPF + WinForms** — на Linux/macOS не собирается и не запускается. |
| **.NET SDK** | **8.0.x** (проверено на `8.0.422`). Ставить .NET 8 SDK, не только Runtime. |
| Архитектура | x64 |

Проверка SDK:
```bash
dotnet --version    # ожидается 8.0.x
```

Целевой фреймворк: `net8.0-windows` (десктоп), `net8.0` (Core-библиотека).
`LangVersion=12`, `Nullable=enable`, `UseWPF=true`, `UseWindowsForms=true`.

---

## 2. Внешние сервисы (нужны для РАБОТЫ, не для сборки)

Приложение **соберётся и запустится без них**, но не будет распознавать речь /
давать ответы, пока не подняты:

| Сервис | Зачем | Адрес по умолчанию |
|---|---|---|
| **LLM API** | генерация ответов | Дефолт: OpenCode Go `https://opencode.ai/zen/go/v1`, модель `deepseek-v4.1-flash` (нужен API-ключ). Локальная альтернатива: любой OpenAI-совместимый сервер (LM Studio, LocalAI) |
| **STT‑сервер** | распознавание речи (аудио→текст) | `http://<host>:8001/v1` (faster‑whisper / GigaAM). Можно поднять локально в **Docker** — см. `LocalSttBridge/` и `SettingsWindow.LocalStt.cs`. |

Без API-ключа облачный дефолт не ответит — приложение попросит ввести ключ
в настройках (или переключитесь на локальный OpenAI-совместимый сервер). Если LLM/STT недоступны —
в оверлее появится **янтарный баннер‑предупреждение** («Транскрипция недоступна» /
«ИИ недоступен»).

---

## 3. Сборка

```bash
git clone <repo-url> brainstorm-buddy
cd brainstorm-buddy

# основное приложение (то, что запускает пользователь)
dotnet build BrainstormBuddy/BrainstormBuddy.csproj -c Debug
```

Ожидаемый результат: `Сборка успешно завершена.` Единственный варнинг
`WFAC010` (про DPI в `app.manifest`) — **безвредный**, игнорируется.

Solution целиком (`BrainstormBuddy.sln`) включает UI‑тесты на **FlaUI** —
для основного сценария их собирать не обязательно.

Release/публикация: см. `PUBLISH_ISSUE_NOTES.md` (там разобраны нюансы
single‑file publish).

---

## 4. Запуск

```bash
BrainstormBuddy/bin/Debug/net8.0-windows/BrainstormBuddy.exe
```

- Окно‑оверлей появляется в **правом верхнем углу**, поверх всех окон.
- **Стелс:** по умолчанию окно исключено из захвата экрана
  (`WDA_EXCLUDEFROMCAPTURE`) — его **не видно в записи экрана и на скриншотах**,
  но видно глазами. Это by design (помощник для интервью).
  - Для скриншота/записи: кнопка‑камера в шапке или **Ctrl+Shift+C**.
  - Тестовый хук: переменная окружения `BUDDY_CAPTURE_VISIBLE=1` оставляет
    окно видимым для захвата с самого старта.

Горячие клавиши: `Ctrl+Shift+H` (скрыть/показать), `Ctrl+Shift+S` (настройки),
`Ctrl+Shift+C` (режим скриншота), `Ctrl+Shift+P` (пауза аудио),
`Ctrl+Shift+Y` (пауза LLM), `Ctrl+Shift+L` (логи).

---

## 5. Конфигурация — ВАЖНЫЙ подводный камень

Рабочий конфиг лежит **НЕ в репозитории**, а в:

```
%APPDATA%\BrainstormBuddy\config.json      (= C:\Users\<user>\AppData\Roaming\BrainstormBuddy\config.json)
```

- `BrainstormBuddy/config.json` в репо — это **лишь стартовый шаблон**.
  Правки в нём **не влияют** на уже запущенное приложение, если в `%APPDATA%`
  уже есть сохранённый конфиг. Это регулярно сбивает с толку — **всегда
  проверяйте `%APPDATA%\BrainstormBuddy\config.json`**.
- Туда же пишутся логи (`logs/`), экспорт транскриптов и AI‑протоколов
  (`transcript_*.txt`, `summary_*.txt`), история (`qa_history.txt`).

Ключевые поля (`Api`, `Audio`, `Ui`, `Hotkeys`, `Advanced`):
- `Api.BaseUrl` / `Api.SttBaseUrl` — адреса LLM и STT.
- `Audio.CaptureMic` — слушать ли **микрофон** (иначе только системный звук/loopback).
- `Ui.WindowOpacity`, `Ui.FontSize`, `Ui.TimelineCollapsed`, `Ui.WindowPosition`.

Многие из этих полей меняются прямо в UI (**Настройки**, `Ctrl+Shift+S`).

---

## 6. Типичные грабли при сборке/итерации

1. **`MSB3027 / MSB3021` — файл занят другим процессом.** Приложение держит
   `BrainstormBuddy.exe` / `*.dll`. Перед пересборкой закройте инстанс:
   ```powershell
   Get-Process BrainstormBuddy -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
2. **PowerShell‑кодировки.** Для UTF‑8‑файлов используйте `-Encoding utf8`.
   Результаты долгих операций пишите в MD, а не в консоль (кириллица в stdout
   PowerShell 5.1 бывает бьётся).
3. **Скриншот показывает «сквозь» окно / чат за ним.** Это стелс
   (exclude‑from‑capture). Снимайте с `BUDDY_CAPTURE_VISIBLE=1` или после
   Ctrl+Shift+C.
4. **Иконки/ресурсы `<Resource>` не копируются в output** — грузить через
   pack‑URI (`pack://application:,,,/...`), а не `File.Exists` по BaseDirectory.

---

## 7. Быстрый чек‑лист «поднять с нуля»

```text
[ ] Windows 10 2004+ / 11
[ ] .NET 8 SDK        → dotnet --version = 8.0.x
[ ] (для работы) API-ключ OpenCode Go (или локальный OpenAI-совместимый сервер) + STT‑движок
[ ] (для работы) STT‑сервер на :8001 (или Docker LocalSttBridge)
[ ] dotnet build BrainstormBuddy/BrainstormBuddy.csproj -c Debug  → «успешно»
[ ] запустить BrainstormBuddy.exe → окно в правом верхнем углу
[ ] проверить %APPDATA%\BrainstormBuddy\config.json (адреса LLM/STT, CaptureMic)
```

Структура решения: `BrainstormBuddy` (WPF UI), `BrainstormBuddy.Core`
(логика/аудио/AI/конфиг), `BrainstormBuddy.UITests` (FlaUI), `Headless`
(консольный прогон пайплайна), `LocalSttBridge` (Docker‑STT), `Tests`.
