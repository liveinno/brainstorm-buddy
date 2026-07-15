# BrainstormBuddy — документация

## Архитектура

```
Микрофон → AudioCaptureEngine → AudioBuffer (VAD) → Channel[64] → WorkerLoop
                                                              ↓
                                                           STT (t-one)
                                                              ↓
                                                           LLM (qwen)
                                                              ↓
                                                         OverlayWindow
```

## Серверы (локальный стенд)

| Назначение | Адрес | Модель | Порт |
|------------|-------|--------|------|
| STT (распознавание речи) | `http://127.0.0.1:2701/v1` | `t-one` | 2701 |
| LLM (языковая модель) | `http://127.0.0.1:3264/api` | `qwen3.5-flash` | 3264 |

### STT: t-one
- Русская модель, 8kHz моно (принимает 16kHz с автоконвертацией)
- ~10× realtime (600s аудио → ~59s обработки)
- OpenAI-совместимый API: `POST /v1/audio/transcriptions`
- Не требует API key
- Параметры: `model=t-one`, `language=ru`, `response_format=json`

### LLM: FreeQwenApi
- OpenAI-совместимый: `POST /api/chat/completions`
- Кастомный эндпоинт: `POST /api/chat` (формат: `{"message":"...","model":"..."}`)
- Не требует API key
- Доступные модели (протестированы):

| Модель | Скорость | Русский | Длинный текст (>500 chars) |
|--------|----------|---------|---------------------------|
| `qwen3.5-flash` | 2с | ✅ | ✅ |
| `qwen3.5-27b` | 2.8с | ✅ | ✅ |
| `qwen3.5-35b-a3b` | 2.1с | ✅ | ✅ |
| `qwen3.5-plus` | 11с | ✅ | ❌ (пустой ответ) |
| `qwen-plus-2025-09-11` | 3.1с | ✅ | ✅ |
| `qwen3-30b-a3b` | 2.2с | ✅ | ✅ |
| `qwen3.5-122b-a10b` | 2.3с | ✅ | ✅ |

**Вывод:** `qwen3.5-plus` возвращает пустой ответ на длинный русский текст (>500 символов).  
Используется `qwen3.5-flash` — самый быстрый и стабильный.

## Системный промпт (интервью)

```
Ты — скрытый AI-ассистент (шепот), который слушает техническое собеседование.
Твоя единственная цель — давать интервьюеру мгновенные шпаргалки, факты,
правильные ответы или советы, как оценить кандидата.
ТЕБЕ ЗАПРЕЩЕНО задавать вопросы.
Если услышал вопрос или обрывки речи — давай краткий и точный ответ на русском.
Формат: 1-2 предложения чистой сути.
```

**Важно:** промпт должен быть КОРОТКИМ (1 абзац). Многострочные промпты с `\n`
и длинными списками запретов вызывают пустой ответ у qwen3.5-plus.

## Настройки по умолчанию (config.json)

Параметры в `%AppData%\BrainstormBuddy\config.json`:

```json
{
  "Api": {
    "BaseUrl": "http://127.0.0.1:3264/api",
    "ApiKey": "",
    "ChatModel": "qwen3.5-flash",
    "SttBaseUrl": "http://127.0.0.1:2701/v1",
    "SttModel": "t-one"
  },
  "Audio": {
    "SampleRate": 16000,
    "ChunkMaxSeconds": 60,
    "SilenceSeconds": 1.8,
    "RmsThreshold": 0.014,
    "VadMode": 3,
    "PreRollMs": 300,
    "PostRollMs": 300,
    "OverlapMs": 500,
    "MinSpeechMs": 200
  }
}
```

**Важно:** STT настройки (`SttBaseUrl`, `SttModel`) force-set в коде после загрузки
конфига — они не зависят от содержимого config.json.

## История проблем и решений

### 1. WebRtcVadSharp — DLL hell (0x8007000B)
**Проблема:** нативная DLL WebRtcVadSharp не грузилась на некоторых системах.
**Решение:** полностью удалён WebRtcVadSharp. Реализован SimpleVad (pure C#) —
RMS + ZCR, 4 режима (0-3).

### 2. Таймлайн зависал после 4-5 минут
**Проблема:** пересоздание UI-элементов каждый тик (Children.Clear + Add).
**Решение:** 240 columns, 500ms/tick, WriteableBitmap. 12 TextBlock для линейки
создаются один раз, обновляется только `.Text`. Все Brush заморожены (frozen),
0 аллокаций за тик.

### 3. Spin-wait на Dispatcher
**Проблема:** `while(!done){Thread.Sleep(1)}` в AddHistoryItem/AddSendingItem.
**Решение:** заменён на прямой `Dispatcher.Invoke`.

### 4. STT принудительно t-one (force override)
**Проблема:** app сохраняла Whisper-настройки при выходе, затирая правки файла.
После перезапуска конфиг снова содержал Whisper.
**Решение:** force-set `SttBaseUrl` + `SttModel` после `loader.Load()`
в `App.xaml.cs`. Позже перенесено ПОСЛЕ создания SettingsViewModel, так как
`DetectPreset()` → `ApplyPreset()` перезаписывал STT обратно на Whisper.

### 5. LLM возвращает пустой ответ
**Проблема:** `qwen3.5-plus` возвращает `"content": ""` при:
- Длинном системном промпте (>500 символов)
- Длинном пользовательском тексте (>500 символов)
- Отсутствии приветствия в сообщении
**Решение:** смена модели на `qwen3.5-flash`, сокращение системного промпта
до 1 абзаца.

### 6. ApiKeyMissing блокировал весь пайплайн
**Проблема:** `MainLoop` проверял `ApiKeyMissing` и дропал все аудио-чанки
при пустом API key. Оверлей не показывался.
**Решение:** убран `if (ApiKeyMissing) { continue; }` из MainLoop.
Оверлей показывается всегда, LLM работает без API key.

### 7. SettingsViewModel перезаписывал STT пресетом
**Проблема:** при создании VM, `DetectPreset()` находил Groq (дефолтный BaseUrl)
и `ApplyPreset()` ставил `SttModel = "whisper-large-v3"`.
**Решение:** force-override перенесён ПОСЛЕ создания VM. Все пресеты обновлены
на `t-one` + порт 2701.

## Тесты

### Интеграционные (нужен доступ к серверам 127.0.0.1)

| Тест | Что делает | Время |
|------|-----------|-------|
| `Llm_ShortSegment_Qa` | 2 мин видео → STT → LLM | ~12с |
| `FullPipeline_SttAndLlm` | 10 мин видео → STT → LLM (3 сегмента) | ~55с |

### Юнит-тесты (без серверов, 43 теста)
- `AudioBufferTests` — VAD, чанки, таймауты
- `SimpleVadTests` — RMS, ZCR, режимы
- `SettingsViewModelTests` — пресеты, детект
- `ConfigLoaderTests` — загрузка/сохранение
- `OpenAiClientTests` — STT/LLM запросы
- `ResamplerTests` — аудио-конвертация
- `LoggingServiceTests` — логирование

## Горячие клавиши

| Комбинация | Действие |
|-----------|----------|
| `Ctrl+Shift+H` | Показать/скрыть оверлей |
| `Ctrl+Shift+O` | Изменить прозрачность |
| `Ctrl+Shift+S` | Открыть настройки |
| `Ctrl+Shift+P` | Пауза аудио |
| `Ctrl+Shift+Y` | Пауза LLM |
| `Ctrl+Shift+C` | Screenshot mode (исключить из захвата экрана) |
| `Ctrl+Shift+L` | Логи |

## Файлы

- `C:\Users\<user>\Documents\BrainstormBuddy\BrainstormBuddy.exe` — собранное приложение
- `%AppData%\BrainstormBuddy\config.json` — конфиг (создаётся автоматически)
- `C:\TestRecordings\` — исходные видео для тестов
- `C:\temp\stt_test_cache\` — кэш WAV-файлов тестов
