# BrainstormBuddy.UITests

> **Vision LLM-based automated UI tester for BrainstormBuddy.**
> Запускает приложение, тыкает кнопки, делает скриншоты, отправляет в Vision LLM для анализа. Ловит UI-баги (каракули, поехавшая вёрстка, нерабочие кнопки) автоматически.

## Quick Start

```bash
dotnet run --project BrainstormBuddy.UITests -c Release
```

Результат — `report.html` рядом с запуском. Exit code:
- `0` — всё прошло, Vision LLM не нашёл проблем
- `1` — были UI-failure'ы (Vision LLM нашёл проблемы **ИЛИ** провайдер умер в процессе)
- `2` — **VISION API НЕДОСТУПЕН** — тест даже не запустился, агенту-разработчику нужно вмешаться

## Что делает тестер

1. **Pre-flight check** — пингует каждый Vision-провайдер маленьким текстовым запросом
   - Если **все** мертвы → exit 2, отчёт с баннером "VISION API UNAVAILABLE"
   - Если хотя бы один жив → продолжаем
2. Запускает `BrainstormBuddy.exe`
3. Находит окно настроек, переключает 6 вкладок
4. На каждой вкладке делает скриншот и отправляет в Vision LLM с промптом:
   - **Проверка кодировки** (ищет каракули, `???`, `рџ`)
   - **Описание UI** (что видно, не поехала ли вёрстка)
5. **Тест переключения тем**:
   - Находит `ThemeCombo` по `AutomationId="ThemeCombo"`
   - Делает скриншот в `HackerTheme`
   - Переключает на `LightTheme`, делает скриншот
   - Возвращает `HackerTheme`
6. **Runtime check** — если в процессе теста Vision API умер, помечает отчёт как требующий ручной проверки
7. Закрывает окно настроек, закрывает приложение, сохраняет отчёт

## Конфигурация

`config.test.json`:

```json
{
  "ExePath": "BrainstormBuddy\\bin\\Release\\net8.0-windows\\BrainstormBuddy.exe",
  "VisionProviders": [
    {
      "BaseUrl": "https://<ваш-openai-совместимый-эндпоинт>/v1/chat/completions",
      "ApiKey": "sk-...",
      "VisionModel": "<vision-модель-провайдера>"
    },
    {
      "BaseUrl": "http://127.0.0.1:8765/v1/chat/completions",
      "ApiKey": "test",
      "VisionModel": "gemini-2.5-flash"
    }
  ]
}
```

- Перебираются **по порядку**. Если первый вернёт 200, второй не трогается.
- Если хочется **только** второй — удали первый.
- API-ключ **test** — потому что прокси на `127.0.0.1:8765` не проверяет ключ.

## Добавление нового сценария

1. Создай `Scenarios/MyScenario.cs`:
   ```csharp
   public class MyScenario
   {
       public async Task RunAsync(AppLauncher launcher, VisionClient vision, HtmlReportBuilder report)
       {
           // ... используй FlaUI чтобы найти элементы, кликать, делать скриншоты
           var imageBase64 = CaptureBase64(window);
           var feedback = await vision.AskAboutImageAsync(imageBase64, "опиши что видишь");
           report.AddStep("My step", imageBase64, feedback);
       }
   }
   ```
2. Добавь вызов в `Program.cs` после `SettingsWindowScenario`.

## Чеклист при сбое

| Симптом | Что делать |
|---------|-----------|
| Exit 2 сразу | Все провайдеры в `config.test.json` мертвы. Замени на живую модель. |
| Exit 1, в отчёте `All providers failed` на полпути | Vision API умер во время теста. Подожди или смени провайдер. |
| Exit 1, в отчёте `ThemeCombo not found` | В `BrainstormBuddy/SettingsWindow.xaml` нет `x:Name="ThemeCombo"` на вкладке "Оверлей". |
| Exit 1, Vision LLM пишет на нерусском | Смени `VisionModel` на модель с лучшей поддержкой русского (gemini-2.5-flash, claude-sonnet-4-6). |
| Exit 1, Vision LLM не нашёл каракули | Увеличь max_tokens в `VisionClient.AskAboutImageAsync` или усиль промпт. |

## Требования

- .NET 8 SDK
- Windows (для запуска `BrainstormBuddy.exe`)
- Хотя бы один рабочий OpenAI-совместимый Vision API endpoint
