# 🐛 Incident Report: dotnet publish не доносит правки до пользователя

**Дата:** 04.06.2026
**Проект:** BrainstormBuddy (WPF, .NET 8)
**Серьёзность:** 🔴 Critical — пользователь видел старый UI, фиксы не работали

---

## 1. Симптомы

Пользователь сообщил, что после моих правок:

- В Settings → Audio **нет секции «Активные устройства»** (с кодом добавлял)
- **Resize окна не работает** (с кодом добавлял `WindowChrome` + `WM_NCHITTEST`)
- **Аудио-чипы на одной строке** со слайдерами, а не на отдельной
- **Контекстное меню** не содержит «Ответить расширенно» на первом месте

Правки точно были в исходниках (я их видел в редакторе, build не падал). Но `BrainstormBuddy.exe` в `C:\Users\<user>\Documents\BrainstormBuddy\` не менялся.

---

## 2. Root Cause (корневая причина)

**`dotnet publish --self-contained true` использует инкрементальную сборку.**

Что происходило:

1. Я редактировал `*.xaml` и `*.cs` файлы в проекте (timestamp: **14:31**)
2. Запускал `dotnet publish` (завершалось за ~2 сек, **0 ошибок**, **0 предупреждений о пропуске файлов**)
3. `dotnet publish` видел, что выходные `.dll`/`.exe` уже есть в `bin\Release\`, и **пропускал пересборку** — даже если исходники изменились
4. Копировал **старые** `.exe` (timestamp **13:58**) в `C:\Users\<user>\Documents\BrainstormBuddy\`
5. Я проверял только наличие файла, а не его timestamp
6. Пользователь запускал старый exe — ничего не менялось

**Ложное ощущение успеха:** `dotnet publish` завершался без ошибок. Build output выглядел штатно. Размер `.exe` совпадал. Единственный способ заметить проблему — сравнить timestamp исходников и `.exe`.

---

## 3. Диагностика

Когда пользователь спросил «может мои правки не доезжают?», я сравнил:

```powershell
$source = Get-Item "C:\Users\<user>\AppData\Local\Temp\opencode\BrainstormBuddy\BrainstormBuddy\BrainstormBuddy\bin\Release\net8.0-windows\win-x64\BrainstormBuddy.exe"
$dest = Get-Item "C:\Users\<user>\Documents\BrainstormBuddy\BrainstormBuddy.exe"

# Source: 06/04/2026 13:58:34 (152576 bytes)
# Dest:   06/04/2026 13:58:34 (152576 bytes)
# Diff:   0 sec
# ✓ identical
```

Но при этом исходники были свежее:

```powershell
Get-Item "SettingsWindow.xaml"        # 14:31:31
Get-Item "obj/.../SettingsWindow.g.cs" # 14:34:22
```

**Доказательство инкрементального бага:** исходники новее, чем собранный `.exe` → сборка не запустилась.

---

## 4. Решение

### Шаг 1: Полная очистка

```powershell
dotnet clean BrainstormBuddy\BrainstormBuddy.csproj -c Release
```

### Шаг 2: Полная пересборка

```powershell
dotnet build BrainstormBuddy.Core\BrainstormBuddy.Core.csproj -c Release
dotnet build BrainstormBuddy\BrainstormBuddy.csproj -c Release
```

### Шаг 3: Публикация (без `--no-build`)

```powershell
dotnet publish BrainstormBuddy\BrainstormBuddy.csproj `
    --self-contained true `
    -r win-x64 `
    -c Release `
    -o "C:\Users\<user>\Documents\BrainstormBuddy"
```

### Шаг 4: Верификация

```powershell
$source = Get-Item "bin\Release\net8.0-windows\win-x64\BrainstormBuddy.exe"
$dest = Get-Item "C:\Users\<user>\Documents\BrainstormBuddy\BrainstormBuddy.exe"

Write-Host "Source: $($source.LastWriteTime) ($($source.Length) bytes)"
Write-Host "Dest:   $($dest.LastWriteTime) ($($dest.Length) bytes)"
```

**Ожидаемый результат:** Source == Dest по timestamp, source.LastWriteTime > timestamp последней правки в исходниках.

### Шаг 5: Сообщить пользователю, что exe обновился

Сказать: «новый timestamp 14:44:07, размер 152576, **обязательно перезапусти** exe, иначе висит старый процесс».

---

## 5. Дополнительный фикс (найден по пути)

После `dotnet clean` всплыли старые ошибки в `DarkDialog.xaml.cs`:
- `Orientation` ambiguous между `System.Windows.Controls` и `System.Windows.Forms`
- `HorizontalAlignment.Right` — обращение через статик

Это значит, что мой предыдущий фикс с alias-ами (`using Orientation = System.Windows.Controls.Orientation;`) **был перезаписан** каким-то более поздним edit-ом. Возможно, в момент когда я убирал `using System.Windows.Forms` или заменял, случайно зацепил alias-ы.

**Исправление:**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orientation = System.Windows.Controls.Orientation;       // alias
using HorizontalAlignment = System.Windows.HorizontalAlignment;  // alias

namespace BrainstormBuddy;
```

---

## 6. Lessons Learned / Профилактика

### Что делать ВСЕГДА после `dotnet publish`:

1. **Проверять timestamp** `BrainstormBuddy.exe` ДО и ПОСЛЕ publish:

   ```powershell
   $before = (Get-Item "...BrainstormBuddy.exe").LastWriteTime
   dotnet publish ...
   $after = (Get-Item "...BrainstormBuddy.exe").LastWriteTime
   
   if ($after -eq $before) {
       Write-Host "⚠️  EXE NOT UPDATED! Forcing clean rebuild..." -ForegroundColor Red
       dotnet clean ... -c Release
       dotnet build ... -c Release
       dotnet publish ... -c Release
   }
   ```

2. **Не доверять** «clean» выводу `dotnet publish`. Успешный exit code + 0 warnings ≠ exe обновлён.

3. **Проверять цепочку:**
   - `*.xaml` / `*.cs` (исходники) — timestamp T1
   - `obj/.../*.g.cs` (сгенерированный) — timestamp T2 ≥ T1
   - `bin/.../*.dll` / `*.exe` (скомпилированный) — timestamp T3 ≥ T2
   - `C:\Users\...BrainstormBuddy.exe` (опубликованный) — timestamp T4 ≥ T3

   Если T4 < T3 → пользователь запускает старую версию.

4. **При первых признаках «правки не работают»** у пользователя — сразу проверять timestamps. Не тратить время на «почему не работает мой код», а сначала убедиться, что свежий код вообще доехал до exe.

5. **Сделать version-маркер в логах.** На старте `App.OnStartup` писать в лог:
   ```
   Logger.Info($"BrainstormBuddy v{AssemblyVersion} started (build: {BuildDate})", "App");
   ```
   Тогда по логам сразу видно, какую версию пользователь запустил.

---

## 7. Команда-автофикс (рекомендую добавить в проект)

Создать `publish.ps1` в корне проекта:

```powershell
$ErrorActionPreference = "Stop"
$project = "BrainstormBuddy\BrainstormBuddy.csproj"
$outDir = "C:\Users\<user>\Documents\BrainstormBuddy"

Write-Host "=== BrainstormBuddy: clean rebuild + publish ===" -ForegroundColor Cyan

$before = (Get-Item "$outDir\BrainstormBuddy.exe" -ErrorAction SilentlyContinue)?.LastWriteTime

dotnet clean $project -c Release --nologo --verbosity quiet
dotnet build "BrainstormBuddy.Core\BrainstormBuddy.Core.csproj" -c Release --nologo --verbosity quiet
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

dotnet publish $project --self-contained true -r win-x64 -c Release -o $outDir --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$after = (Get-Item "$outDir\BrainstormBuddy.exe").LastWriteTime

if ($before -eq $after) {
    Write-Host "⚠️  WARN: exe timestamp unchanged!" -ForegroundColor Red
} else {
    Write-Host "✓ Exe updated: $before → $after" -ForegroundColor Green
}
Write-Host "Done."
```

---

## 8. Итог

| Параметр | Значение |
|---|---|
| **Потерянное время** | ~30 минут на 3-4 итерации «правлю → публикую → пользователь не видит» |
| **Причина** | `dotnet publish` + `--self-contained` = инкрементальная сборка с silent skip |
| **Решение** | `clean → build Core → build WPF → publish` (полный цикл) |
| **Профилактика** | Проверка timestamp exe после каждого publish + version-маркер в логах |
| **Созданный артефакт** | `publish.ps1` (рекомендуется) |

---

## Changelog

- **04.06.2026 14:44** — фикс опубликован, exe timestamp обновлён
- **04.06.2026 14:31** — последние правки в исходниках (SettingsWindow.xaml, OverlayWindow.xaml)
- **04.06.2026 13:58** — старый «невидимый» publish (доехал только когда clean+rebuild принудительно)
