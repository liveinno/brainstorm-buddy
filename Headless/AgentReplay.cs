using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrainstormBuddy.Ai;
using BrainstormBuddy.Config;

namespace BrainstormBuddy.Headless;

/// <summary>
/// Прогон реальных реплик из транскрипта через НАСТОЯЩИЙ AgentOrchestrator (интервью-сценарий,
/// TechLead + HRD) на локальном Ollama qwen 3b — как в бою. Пишет JSON (реплика → ответы агентов)
/// для последующей оценки суд-агентами. Приватность: JSON остаётся на диске (%APPDATA%\eval),
/// в git не коммитится.
/// </summary>
public static class AgentReplay
{
    // Объединённый промпт для режима одного агента (--single): и техника, и HR, и молчание в одном.
    private const string SingleSuffleurPrompt =
        "Ты — суфлёр КАНДИДАТА на собеседовании. Твой вывод — готовая реплика, которую кандидат " +
        "произнесёт ОТ ПЕРВОГО ЛИЦА. Ты НЕ интервьюер, НЕ бот: не приветствуй, не представляйся, " +
        "не упоминай ИИ/свою роль, не задавай вопросы кандидату.\n\n" +
        "Определи тип последней реплики интервьюера и действуй:\n" +
        "- ТЕХНИЧЕСКИЙ/проектный вопрос → ответ одним абзацем STAR (Ситуация→Задача→Действие→" +
        "Результат) с КОНКРЕТНОЙ цифрой/фактом из профиля, ≤{{MAX_WORDS}} слов.\n" +
        "- HR/поведенческий вопрос (мотивация, причина ухода, слабые/сильные стороны, планы, " +
        "зарплата, «расскажите о себе», манипуляция) → короткий совет-реплика, 1-2 предложения; " +
        "при явном давлении добавь блок [ЗАЩИТА].\n" +
        "- НЕ вопрос (речь самого кандидата, филлер, обрывок) ИЛИ непонятная/garbled-реплика → " +
        "выведи РОВНО одну строку [SILENT] и больше ничего.\n\n" +
        "Строго: числа и названия бери ДОСЛОВНО из профиля, ничего не выдумывай; не угадывай " +
        "непонятные термины и аббревиатуры (сомневаешься — [SILENT]); без воды и слов-паразитов; " +
        "не повторяй предыдущий ответ.\n" +
        "Стиль: {{STYLE}}. Тон: {{TONE}}. Язык: {{LANGUAGE}}.\n\n{{USER_PROFILE}}\n\n{{EXTRA_INSTRUCTIONS}}";

    private sealed record Turn(string Timestamp, string Question, bool LooksLikeQuestion,
        string TechLead, bool TechLeadSilent, long TechLeadMs,
        string Hrd, bool HrdSilent, long HrdMs);

    // Строка размеченного транскрипта (диаризация): роль спикера + признак вопроса.
    private sealed record LabeledLine(string t, string text, string role, bool isQuestion);
    private sealed record LabeledFile(string video, List<LabeledLine> lines);

    public static int Run(string[] rest, string appDataDir)
    {
        if (rest.Contains("--labeled")) return RunLabeled(rest, appDataDir);

        string target = rest.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        if (string.IsNullOrWhiteSpace(target)) { Console.WriteLine("--agent-replay <transcript|folder> [--maxlines 40] [--questions-only]"); return 2; }

        int maxLines = ArgInt(rest, "--maxlines", 40);
        bool qOnly = rest.Contains("--questions-only");
        string baseUrl = ArgStr(rest, "--url", "http://127.0.0.1:11434/v1");
        string model = ArgStr(rest, "--model", "qwen2.5vl:3b");
        string tag = ArgStr(rest, "--tag", "");   // суффикс имени файла (напр. "7b"), чтобы не затирать
        bool single = rest.Contains("--single"); // один объединённый агент вместо двух (без «оркестрации»)
        int seqDelayMs = ArgInt(rest, "--seqdelay", 4000); // пауза между запросами к прокси (мс)
        int retries = ArgInt(rest, "--retries", 3);        // ретраи на пустой ответ/ошибку

        var files = new List<string>();
        if (Directory.Exists(target)) files.AddRange(Directory.GetFiles(target, "*_timestamped.txt"));
        else if (File.Exists(target)) files.Add(target);
        if (files.Count == 0) { Console.WriteLine($"Нет транскриптов: {target}"); return 2; }

        var evalDir = Path.Combine(appDataDir, "eval");
        Directory.CreateDirectory(evalDir);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file).Replace("_timestamped", "");
            Console.WriteLine($"\n=== {name} ===");
            var lines = ParseLines(file);
            // Отбор ИНДЕКСОВ вопросоподобных строк (не короче 20 симв, вне интро ~90с), равномерно.
            var poolIdx = Enumerable.Range(0, lines.Count).Where(i => qOnly
                ? LooksLikeQuestion(lines[i].Text) && lines[i].Text.Length >= 20 && SecondsOf(lines[i].Timestamp) > 90
                : lines[i].Text.Length >= 20).ToList();
            HashSet<int> pickedIdx;
            if (poolIdx.Count > maxLines)
            {
                double step = poolIdx.Count / (double)maxLines;
                pickedIdx = Enumerable.Range(0, maxLines).Select(k => poolIdx[(int)(k * step)]).ToHashSet();
            }
            else pickedIdx = poolIdx.ToHashSet();
            Console.WriteLine($"  реплик всего={lines.Count}, берём={pickedIdx.Count} (questionsOnly={qOnly}, model={model})");
            if (pickedIdx.Count == 0) continue;

            // Свежий оркестратор на каждый файл (чистая история, как новая сессия).
            var cfg = MultiAgentConfig.CreateDefaults();
            cfg.Enabled = true;
            cfg.ActiveScenarioId = "interview";
            if (single)
            {
                // Режим ОДНОГО агента (без «оркестрации» двух ролей): один объединённый суфлёр.
                var sc = cfg.Scenarios.First(s => s.Id == "interview");
                sc.Agents = new List<AgentConfig> { new AgentConfig {
                    Id = "suffleur", Name = "Суфлёр", Color = "#4ADE80",
                    MaxWords = 120, Tone = "Деловой", Style = "Структурированный", Language = "ru",
                    SystemPrompt = SingleSuffleurPrompt } };
            }
            var orch = new AgentOrchestrator(cfg, apiKey: "x", baseUrl: baseUrl)
            {
                // Бережём прокси: агентов зовём ПОСЛЕДОВАТЕЛЬНО, с паузой и ретраями на пустой ответ.
                Sequential = true,
                InterRequestDelayMs = seqDelayMs,
                MaxRetries = retries,
                RetryDelayMs = 15000,
                Log = s => { if (s.Contains("ERROR") || s.Contains("rate") || s.Contains("HTTP")) Console.WriteLine($"\n  [orch] {s}"); },
            };

            var turns = new List<Turn>();
            var sw = Stopwatch.StartNew();
            int done = 0;
            // Прогоняем ВЕСЬ транскрипт по порядку: не-выбранные реплики уходят в контекст (NoteContext),
            // на выбранных вопросах генерим ответ — так у агента есть контекст диалога, как в бою.
            for (int idx = 0; idx < lines.Count; idx++)
            {
                var labeled = $"[Динамик] {lines[idx].Text}";  // в замере спикеров нет — всё как [Динамик]
                if (!pickedIdx.Contains(idx))
                {
                    orch.NoteContext(labeled);
                    continue;
                }
                done++;
                List<AgentResponse> res;
                try { res = orch.ProcessAsync(labeled, model).GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"\n  [{lines[idx].Timestamp}] ошибка: {ex.Message}"); continue; }

                var tech = res.FirstOrDefault(r => r.AgentId == "tech_lead") ?? (single ? res.FirstOrDefault() : null);
                var hrd = res.FirstOrDefault(r => r.AgentId == "hrd");
                turns.Add(new Turn(lines[idx].Timestamp, lines[idx].Text, LooksLikeQuestion(lines[idx].Text),
                    tech?.Text ?? "", tech?.IsSilent ?? true, tech?.LatencyMs ?? 0,
                    hrd?.Text ?? "", hrd?.IsSilent ?? true, hrd?.LatencyMs ?? 0));
                Console.Write($"\r  {done}/{pickedIdx.Count} реплик обработано ({sw.Elapsed.TotalSeconds:F0}с)…   ");
                if (done < pickedIdx.Count) System.Threading.Thread.Sleep(seqDelayMs); // пауза до следующей реплики
            }
            Console.WriteLine();

            var suffix = string.IsNullOrEmpty(tag) ? "" : "_" + tag;
            var outPath = Path.Combine(evalDir, $"agent_replay_{name}{suffix}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { video = name, model, turns },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(false));

            int answeredBoth = turns.Count(t => !t.TechLeadSilent && !t.HrdSilent);
            int silentBoth = turns.Count(t => t.TechLeadSilent && t.HrdSilent);
            int qSilent = turns.Count(t => t.LooksLikeQuestion && t.TechLeadSilent && t.HrdSilent);
            Console.WriteLine($"  → {outPath}");
            Console.WriteLine($"  оба ответили={answeredBoth}, оба молчали={silentBoth}, из них молчали на ВОПРОС={qSilent}");
        }
        return 0;
    }

    // Прогон по РАЗМЕЧЕННОМУ транскрипту (Ступень 1): вопрос рекрутера → агент отвечает,
    // речь кандидата/не-вопрос → только контекст. Так спикеры разделены правильно (как в бою).
    private static int RunLabeled(string[] rest, string appDataDir)
    {
        string dir = ArgStr(rest, "--labeled", "");
        string baseUrl = ArgStr(rest, "--url", "http://127.0.0.1:11434/v1");
        string model = ArgStr(rest, "--model", "qwen2.5vl:3b");
        string apiKey = ArgStr(rest, "--key", "x");   // Bearer-ключ (для внешнего облачного API и т.п.)
        int seqDelayMs = ArgInt(rest, "--seqdelay", 4000);
        int retries = ArgInt(rest, "--retries", 3);
        int maxq = ArgInt(rest, "--maxq", 12);   // кап вопросов на видео (равномерная выборка), бережём лимит
        string tag = ArgStr(rest, "--tag", "labeled");
        var jopt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var evalDir = Path.Combine(appDataDir, "eval"); Directory.CreateDirectory(evalDir);

        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "labeled_*.json")
                  : File.Exists(dir) ? new[] { dir } : Array.Empty<string>();
        if (files.Length == 0) { Console.WriteLine($"Нет размеченных файлов labeled_*.json в {dir}"); return 2; }

        foreach (var file in files)
        {
            LabeledFile? doc;
            try { doc = JsonSerializer.Deserialize<LabeledFile>(File.ReadAllText(file), jopt); }
            catch (Exception ex) { Console.WriteLine($"Плохой JSON {file}: {ex.Message}"); continue; }
            if (doc?.lines == null || doc.lines.Count == 0) { Console.WriteLine($"Пусто: {file}"); continue; }

            // Индексы вопросов рекрутера; если их больше кап — берём равномерную выборку.
            var qIdxAll = Enumerable.Range(0, doc.lines.Count)
                .Where(i => doc.lines[i].role == "recruiter" && doc.lines[i].isQuestion).ToList();
            HashSet<int> selected;
            if (qIdxAll.Count > maxq)
            {
                double step = qIdxAll.Count / (double)maxq;
                selected = Enumerable.Range(0, maxq).Select(k => qIdxAll[(int)(k * step)]).ToHashSet();
            }
            else selected = qIdxAll.ToHashSet();
            int rq = selected.Count;
            Console.WriteLine($"\n=== {doc.video} ===  строк={doc.lines.Count}, вопросов рекрутера={qIdxAll.Count}→берём {rq}, model={model}");
            if (rq == 0) { Console.WriteLine("  нет вопросов рекрутера — пропуск"); continue; }

            var cfg = MultiAgentConfig.CreateDefaults(); cfg.Enabled = true; cfg.ActiveScenarioId = "interview";
            var orch = new AgentOrchestrator(cfg, apiKey: apiKey, baseUrl: baseUrl)
            {
                Sequential = true, InterRequestDelayMs = seqDelayMs, MaxRetries = retries, RetryDelayMs = 15000,
                Log = s => { if (s.Contains("ERROR") || s.Contains("rate") || s.Contains("HTTP")) Console.WriteLine($"\n  [orch] {s}"); },
            };

            var turns = new List<Turn>(); var sw = Stopwatch.StartNew(); int done = 0;
            for (int idx = 0; idx < doc.lines.Count; idx++)
            {
                var ln = doc.lines[idx];
                string label = ln.role == "candidate" ? "[Микрофон]" : "[Динамик]";
                var labeled = $"{label} {ln.text}";
                if (selected.Contains(idx))
                {
                    done++;
                    List<AgentResponse> res;
                    try { res = orch.ProcessAsync(labeled, model).GetAwaiter().GetResult(); }
                    catch (Exception ex) { Console.WriteLine($"\n  [{ln.t}] ошибка: {ex.Message}"); continue; }
                    var tech = res.FirstOrDefault(r => r.AgentId == "tech_lead");
                    var hrd = res.FirstOrDefault(r => r.AgentId == "hrd");
                    turns.Add(new Turn(ln.t, ln.text, true,
                        tech?.Text ?? "", tech?.IsSilent ?? true, tech?.LatencyMs ?? 0,
                        hrd?.Text ?? "", hrd?.IsSilent ?? true, hrd?.LatencyMs ?? 0));
                    Console.Write($"\r  {done}/{rq} вопросов обработано ({sw.Elapsed.TotalSeconds:F0}с)…   ");
                    if (done < rq) System.Threading.Thread.Sleep(seqDelayMs);
                }
                else orch.NoteContext(labeled);  // речь кандидата / не-вопрос → в контекст, без ответа
            }
            Console.WriteLine();
            var outPath = Path.Combine(evalDir, $"agent_replay_{doc.video}_{tag}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { video = doc.video, model, turns },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                new UTF8Encoding(false));
            int answered = turns.Count(t => !t.TechLeadSilent || !t.HrdSilent);
            Console.WriteLine($"  → {outPath}  (ответили на {answered}/{turns.Count} вопросов рекрутера)");
        }
        return 0;
    }

    private sealed record Line(string Timestamp, string Text);

    private static double SecondsOf(string ts)
    {
        var p = ts.Split(':');
        return p.Length == 3 ? int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60 + int.Parse(p[2])
             : p.Length == 2 ? int.Parse(p[0]) * 60 + int.Parse(p[1]) : 0;
    }

    private static List<Line> ParseLines(string path)
    {
        var rx = new Regex(@"^\[?(?:(\d{1,2}):)?(\d{1,2}):(\d{2})\]?\s+(.*)$");
        var outp = new List<Line>();
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var m = rx.Match(raw.Trim());
            if (!m.Success) continue;
            var ts = (m.Groups[1].Success ? m.Groups[1].Value + ":" : "") + m.Groups[2].Value + ":" + m.Groups[3].Value;
            var t = m.Groups[4].Value.Trim();
            if (t.Length >= 4) outp.Add(new Line(ts, t));
        }
        return outp;
    }

    private static bool LooksLikeQuestion(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains('?')) return true;
        string[] qw = { "что ", "как ", "како", "почему", "зачем", "расскажи", "объясни",
            "сколько", "чем ", "когда", "где ", "какой", "опиши", "перечисли", "назови", "ваш" };
        return qw.Any(w => t.Contains(w));
    }

    private static string ArgStr(string[] a, string key, string def)
    { var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault(); return string.IsNullOrEmpty(v) || v.StartsWith("--") ? def : v; }
    private static int ArgInt(string[] a, string key, int def)
    { var v = a.SkipWhile(x => x != key).Skip(1).FirstOrDefault(); return int.TryParse(v, out var r) ? r : def; }
}
