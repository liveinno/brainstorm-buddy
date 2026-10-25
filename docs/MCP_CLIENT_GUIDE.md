# MCP-клиент: инструменты, ограничения, примеры

Документ для ИИ-агентов (Codex, Claude и любые MCP-клиенты), которые подключаются
к BrainstormBuddy как к MCP-серверу. Описывает **фактическое** поведение
инструментов Buddy MCP (версия 2.36.4).

- Источник истины по поведению: `BrainstormBuddy.Core/Mcp/Http/McpHttpToolBridge.cs`
  (описания инструментов), `BrainstormBuddy.Core/Mcp/LocalMcpFacade.cs`
  (поиск/чтение/запись в память) и `BrainstormBuddy.McpHost/BuddyTools.cs`
  (stdio-обёртка для MCP-клиентов).
- Готовый system prompt для агента: **Настройки → MCP → «Скопировать инструкции для ИИ»**
  (тот же текст лежит в `BrainstormBuddy.Core/Mcp/McpClientInstructions.cs`).

---

## 1. Подключение

| Транспорт | Как подключиться | Авторизация |
|---|---|---|
| named pipe (рекомендуется) | `\\.\pipe\buddy-mcp-<UserSid>` (точный SID — в карточке подключения и в логе `MCP incoming on pipe …`) | `connectionToken` в `params` каждого вызова |
| HTTP (loopback) | `http://127.0.0.1:8734` (`Mcp.HttpEnabled`, `Mcp.HttpPort`) | `Authorization: Bearer <token>` |

Токен — 32 случайных байта, base64url. Выдаётся мастером «Подключить AI-агент»,
хранится в DPAPI-хранилище `%AppData%\BrainstormBuddy\mcp\permissions.json`
(в файле — только SHA-256 хэш и DPAPI-блоб). **Токен можно выдать и отозвать
на лету**: MCP перечитывает файл при каждом запросе, перезапуск приложения не нужен.

### Путь к хосту: постоянный, не временный

MCP-клиент запускает локальный хост `BrainstormBuddy.McpHost.exe`. В генерируемые
мастером конфиги попадает **установленная** копия хоста:

```
%LOCALAPPDATA%\Programs\BrainstormBuddy\BrainstormBuddy.McpHost.exe
```

(например `C:\Users\<пользователь>\AppData\Local\Programs\BrainstormBuddy\BrainstormBuddy.McpHost.exe`).
Временные и рабочие пути (папка распаковки single-file, `bin\…` и т.п.) в конфиг
не попадают: после перезагрузки, обновления или переустановки путь продолжает
работать. Если установленной копии нет (например, запуск из исходников) — в конфиг
попадает путь рядом с запущенным exe.

Токен в командной строке не передаётся: хост читает `.token`-файл из `argv[0]`.
Файл токена лежит в `%APPDATA%\BrainstormBuddy\mcp\tokens\<id>.token` и доступен
только текущему пользователю (DPAPI-стор).

Проверить канал: **Настройки → MCP → «Проверить MCP»** — отчёт по каналу, токенам,
журналу, инструментам и путям. Журналы вызовов (токены и ключи маскируются как `***`):
- `%AppData%\BrainstormBuddy\logs\mcp.log` — сторона Buddy (входящий канал);
- `%AppData%\BrainstormBuddy\logs\mcphost.log` — сторона McpHost (stdio-клиент).

Оба журнала пишут один и тот же `request ID` вызова, поэтому запись хоста и запись
Buddy сопоставляются напрямую.

### Минимальный пример (named pipe, JSON-RPC 2.0 построчно)

```json
{"jsonrpc":"2.0","id":1,"method":"facade.status","params":{"connectionToken":"<token>"}}
```

Ответ:

```json
{"jsonrpc":"2.0","id":1,"result":{"version":"2.36.3","meetings":128,"projects":7}}
```

### HTTP: `tools/list` и `tools/call`

```http
POST http://127.0.0.1:8734/mcp
Authorization: Bearer <token>
Content-Type: application/json

{"jsonrpc":"2.0","id":1,"method":"tools/list"}
```

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call",
 "params":{"name":"buddy_search","arguments":{"query":"бюджет","mode":"all","limit":25}}}
```

Имена аргументов в `tools/call` — snake_case (`project_id`, `source_ref`),
внутри facade-методов — camelCase (`projectId`, `sourceRef`).

### Codex (CLI): подключение вручную

Отдельной записи «Codex (CLI)» в мастере нет — выберите «Другой агент»
(«На этом компьютере»), пройдите шаги доступа и подключения, затем на шаге 3
возьмите из блока «Дополнительно» команду запуска и путь к файлу токена.
Их нужно вписать в конфиг Codex `%USERPROFILE%\.codex\config.toml` самому:

```toml
[mcp_servers.brainstorm-buddy]
command = "C:\\Users\\<пользователь>\\AppData\\Local\\Programs\\BrainstormBuddy\\BrainstormBuddy.McpHost.exe"
args = ["C:\\Users\\<пользователь>\\AppData\\Roaming\\BrainstormBuddy\\mcp\\tokens\\<id>.token"]
```

Команда указывает на **установленную** копию хоста (постоянный путь), а не на
рабочую папку сборки: после перезагрузки хост запускается нормально.
Секцию `[mcp_servers.brainstorm-buddy]` можно дописать в конец файла — остальное
содержимое `config.toml` (другие MCP-серверы, настройки модели) не затрагивается.

---

## 2. Инструменты (13 шт.)

| Инструмент | Назначение | Ключевые аргументы |
|---|---|---|
| `buddy_status` | версия, счётчики, состояние туннеля | — |
| `buddy_list_projects` | список проектов, доступных подключению | — |
| `buddy_list_meetings` | встречи (транскрипты), новые первыми | `limit` (1..200, по умолчанию 200), `cursor` (offset = число уже полученных встреч), `project_id` |
| `buddy_search` | поиск по транскриптам, памяти, текстам вложений, проектам | `query` (обязателен), `type`, `mode`, `from`, `to`, `project_ids`, `cursor`, `limit` |
| `buddy_read_source` | чтение источника из `reference` | `reference` (обязателен), `offset`, `cursor`, `limit` (байты) |
| `buddy_list_documents` | вложения встречи + статус извлечения текста | `meeting_id` |
| `buddy_add_fact` | сохранить факт в память (идемпотентно) | `text` (обязателен), `source_ref`, `project_id`, `meeting_id` |
| `buddy_update_fact` | изменить текст факта в памяти по его id | `fact_id` (обязателен), `text` (обязателен) |
| `buddy_delete_fact` | удалить факт из памяти по его id (мягко, с подтверждением владельца) | `fact_id` (обязателен) |
| `buddy_read_tool_result` | дочитать сохранённый большой результат | `id` (tool_result_id), `offset`, `limit` |
| `buddy_run_job` | поставить фоновую задачу | `kind` (`search`/`research`/`transcribe`/`extract_facts`/`resume`/`continue`), `args_json`, `job_id`, `idempotency_key` |
| `buddy_job_status` | состояние фоновой задачи | `job_id` |
| `buddy_cancel_job` | отменить фоновую задачу | `job_id` |

### `buddy_search` — режимы и фильтры

- `mode`: `relevant` (по умолчанию) — top-N по рангу, `limit` 1..50;
  `all` — обход всех совпадений с постраничным `nextCursor`, `limit` 1..200.
- `type`: `meeting`|`transcript`, `memory`|`fact`, `document`|`file`, `project`, `all`
  (через запятую). По умолчанию `meeting` + `memory`.
- Ответ содержит `totalMatches`, `hasMore`, `nextCursor`, `exhaustive`.
  `totalMatches` — число совпадений, найденных **начиная с начала обхода**:
  на последней странице это полное число совпадений, на промежуточных — найденное
  на этот момент. `exhaustive: false` = покрытие неполное (например, ограничение FTS) —
  **продолжай постранично**, не считай результат полным.
- У каждого попадания есть стабильный `reference`: `meeting:{id}`, `memory:{id}`,
  `attachment:{docId}`.

### `buddy_read_source` — постраничное чтение

- Ответ: `offset` (символы), `totalChars`, `hasMore`, `nextCursor`.
- Бюджет страницы по умолчанию — 131 072 байта, максимум 2 097 152.
- `nextCursor` предпочтительнее `offset`.
- Если текст источника недоступен: `truncated: true` + `truncatedReason` —
  **сообщи об этом пользователю**, не выдумывай содержимое.

### `buddy_add_fact`

Идемпотентна: тот же текст в том же scope вернёт `duplicate: true` и id
существующего факта.

### `buddy_update_fact`

- Меняет текст факта в памяти Buddy по его id (числовой id из ответа
  `buddy_add_fact` или из `buddy_search`; можно передать и `reference`
  вида `memory:42` — хост возьмёт число после двоеточия).
- Требует права `change` («Изменение данных»). Нет права — отказ
  `SCOPE_DENIED`; факт вне области подключения — тоже отказ `SCOPE_DENIED`.
- Факта нет или он уже неактивен (удалён/заменён) — `notFound: true`,
  данные не меняются.
- Ответ: `{id, updated, notFound, scope, reference, message}`.

### `buddy_delete_fact`

- Помечает факт удалённым: статус становится «недействителен», запись в базе
  остаётся (удаление мягкое).
- Требует права `delete` («Удаление») **и** явного подтверждения владельца:
  в оверлее Buddy появляется карточка с текстом факта. Отказ, отмена или
  таймаут 5 минут → удаление отклоняется, факт остаётся, данные не меняются.
- Факта нет или он уже удалён — `notFound: true`.
- Ответ: `{id, deleted, notFound, scope, reference, message}`.

### Права подключения

Права выдаёт владелец в мастере «Подключить AI-агент» (шаг 2 «Доступ»):
чтение данных (`read`), добавление фактов (`add-fact`), изменение данных
(`change`), удаление (`delete`) и отдельный флаг «включать память».

- **Пустой набор прав сохраняется как есть** и не расширяется автоматически:
  если снять все галочки, подключение создаётся без полномочий — агент не
  сможет читать и изменять данные. Молчаливого добавления права на чтение
  нет; на шаге прав об этом показывается явная подсказка.
- **Право на чтение нужно только для чтения.** Добавление факта
  (`buddy_add_fact`) требует лишь права `add-fact` — видеть ваши данные для
  этого не обязательно; изменение (`buddy_update_fact`) — права `change`;
  удаление (`buddy_delete_fact`) — права `delete`. Область (`scope`)
  подключения проверяется всегда, независимо от набора прав.
- **Удаление всегда требует подтверждения в Buddy**, независимо от выданных
  прав: карточка в оверлее, отказ/отмена/таймаут 5 минут → факт остаётся.
- Инструменты памяти (`buddy_add_fact`, `buddy_update_fact`,
  `buddy_delete_fact`) работают только при включённом флаге «включать память».
- Права проверяются на каждом вызове: отказ приходит ошибкой, а не пустым
  результатом.

### Фоновые задачи

`buddy_run_job` → `buddy_job_status` (poll) → `buddy_cancel_job`.
Виды: `search`/`research` (args `{"query":"…"}`), `transcribe`
(`{"file_path":"…"}` или `{"import_id":"…"}`), `extract_facts`
(`{"meeting_id":N}` или `{"all":true}`), `resume`/`continue` (нужен `job_id`).

---

## 3. Ограничения

| Ограничение | Значение |
|---|---|
| Страница `buddy_list_meetings` | 1..200 встреч (по умолчанию 200) |
| Страница `buddy_search` (`relevant`) | 1..50 попаданий (по умолчанию 50) |
| Страница `buddy_search` (`all`) | 1..200 попаданий (по умолчанию 200) |
| Страница `buddy_read_source` | 131 072 байт по умолчанию, максимум 2 097 152 |
| Страница `buddy_read_tool_result` | 131 072 байт по умолчанию, максимум 2 097 152 |
| Права подключения | `read`, `add-fact`, `change`, `delete` — выдаёт владелец в мастере |
| Пустой набор прав | сохраняется как есть, автоматически не расширяется (подключение без полномочий) |
| Область (`scope`) | `all`, `all-projects`, `existing-projects`, `selected-projects`, `selected-meetings` |
| Память в поиске | включается флагом подключения `memory` |
| Доверие | `own`, `trusted-remote`, `third-party` (влияет на подтверждения в чате) |
| Удаление | всегда с подтверждением в чате Buddy |
| Подтверждение удаления | карточка в оверлее; отказ, отмена или таймаут 5 минут → факт остаётся |

Права и область проверяются на каждом вызове: отказ приходит ошибкой, а не
пустым результатом. Если прав нет — **не переспрашивай по кругу**, сообщи
пользователю, что недоступно.

---

## 4. Ошибки

Формат — JSON-RPC 2.0:

```json
{"jsonrpc":"2.0","id":7,"error":{
  "code":-32601,
  "message":"unknown method 'facade.noSuchMethod'",
  "data":{"requestId":"mcph-3f9a21c4"}}}
```

- `data.requestId` — ID запроса клиента (или `srv-xxxxxxxx`, если клиент его не прислал).
  Тот же ID пишется в журнал `logs/mcp.log` (`req=… transport=… method=… ok|failed ms=…`).
  **Цитируйте requestId**, когда сообщаете о проблеме.
- Частые коды: `-32601` unknown method, `-32602` invalid params, `-32001`
  unauthorized (неизвестный/отозванный токен, подключение приостановлено),
  `-32002` scope denied / нет прав (выход за scope подключения или снятое право).
- Внутренние ошибки возвращаются с причиной (`internal error — <тип>: <сообщение>`),
  полный трейс — в журнале.

---

## 5. Примеры запросов

**Найти все обсуждения бюджета и прочитать их целиком:**

```json
{"jsonrpc":"2.0","id":1,"method":"facade.search",
 "params":{"connectionToken":"<token>","query":"бюджет","type":"transcript",
           "mode":"all","limit":25}}
```

Затем для каждого попадания:

```json
{"jsonrpc":"2.0","id":2,"method":"facade.readSource",
 "params":{"connectionToken":"<token>","reference":"meeting:42","limit":262144}}
```

…и повторять с `nextCursor`, пока `hasMore` не станет `false`.

**Список встреч проекта (постранично):**

```json
{"jsonrpc":"2.0","id":3,"method":"facade.listMeetings",
 "params":{"connectionToken":"<token>","projectId":7,"limit":50,"cursor":"50"}}
```

**Вложения встречи и их текст:**

```json
{"jsonrpc":"2.0","id":4,"method":"facade.listMeetingDocuments",
 "params":{"connectionToken":"<token>","meetingId":42}}
```

**Сохранить факт со ссылкой на встречу:**

```json
{"jsonrpc":"2.0","id":5,"method":"facade.addFact",
 "params":{"connectionToken":"<token>","text":"Бюджет на Q3 утверждён 12 июня",
           "sourceRef":"meeting:42#t=45.0","meetingId":42}}
```

**Изменить текст сохранённого факта:**

```json
{"jsonrpc":"2.0","id":7,"method":"facade.updateFact",
 "params":{"connectionToken":"<token>","factId":"42",
           "text":"Бюджет на Q3 утверждён 20 июня"}}
```

**Удалить факт (нужно подтверждение владельца в Buddy):**

```json
{"jsonrpc":"2.0","id":8,"method":"facade.deleteFact",
 "params":{"connectionToken":"<token>","factId":"42"}}
```

Удаление без карточки подтверждения в оверлее Buddy отклоняется: агент получит
ошибку, а факт останется на месте.

**Фоновая транскрибация файла:**

```json
{"jsonrpc":"2.0","id":6,"method":"facade.enqueueJob",
 "params":{"connectionToken":"<token>","kind":"transcribe",
           "argsJson":"{\"file_path\":\"C:\\\\Temp\\\\call.wav\"}"}}
```

---

## 6. System prompt для агента

Полный текст system prompt копируется кнопкой «Скопировать инструкции для ИИ»
(Настройки → MCP) — там же перечислены все `buddy_*`-инструменты, включая
`buddy_update_fact` и `buddy_delete_fact`. Ключевые правила:

1. Сначала поиск, потом чтение; id встреч не выдумывать — брать `reference` из выдачи.
2. Постранично до `hasMore: false`; `exhaustive: false` — покрытие неполное.
3. `mode: "all"` — когда нужен полный перебор, `relevant` — когда нужен быстрый ответ.
4. `buddy_read_source` — дочитывать по `nextCursor`.
5. `truncated: true` — сообщить пользователю, а не додумывать содержимое.
6. `buddy_add_fact` идемпотентна.
7. Долгие операции — через `buddy_run_job` + `buddy_job_status`.
8. В ошибке есть `requestId` — цитировать его.
9. Не повторять отказ по правам по кругу.
10. Не выдумывать факты, даты, цитаты; ссылаться на `reference`.
11. `fact_id` для `buddy_update_fact`/`buddy_delete_fact` брать из ответа
    `buddy_add_fact` или из выдачи поиска — id не выдумывать.
12. Удаление факта всегда упирается в подтверждение владельца в Buddy: отказ,
    отмена или таймаут — факт остался; сообщи об этом пользователю.
