def chapters(h):
    return [
        ("The “API” tab — connecting to an LLM", "".join([
            h.p("Here you connect BrainstormBuddy to a language model (LLM): "
                "you specify the provider, the API address, the key and the model. Everything the "
                "assistant recognizes goes exactly here, so these settings affect both "
                "answer quality and conversation privacy. Speech recognition is not "
                "configured on this tab — STT is set up in the separate "
                "“Speech recognition (STT)” section."),
            h.img("03-tab-api.png", "Settings window, “API” tab", "wide"),

            h.h2("Choosing a provider"),
            h.p("The “LLM provider (fills in BaseUrl)” drop-down list is the fastest "
                "way to set everything up: pick a provider and BrainstormBuddy itself "
                "puts the API address into the “BaseUrl” field and the first model from the preset into "
                "“Chat Model”. Built-in profiles:"),
            h.table(
                ["Provider", "BaseUrl", "Model presets", "Key"],
                [
                    ["OpenCode Go", "https://opencode.ai/zen/go/v1",
                     "deepseek-v4.1-flash, MiMo-V2.6-Flash", "Required (Bearer)"],
                    ["Ollama (local)", "http://127.0.0.1:11434/v1",
                     "deepseek-v4.1-flash", "Not required"],
                    ["LM Studio (local)", "http://localhost:1234/v1",
                     "—", "Not required"],
                    ["OpenRouter", "https://openrouter.ai/api/v1",
                     "nvidia/nemotron-3.5-lightning:free, google/gemma-4-26b-a4b-it:free, openrouter/free", "Required (Bearer)"],
                    ["Groq", "https://api.groq.com/openai/v1",
                     "openai/gpt-oss-120b, openai/gpt-oss-20b", "Required (Bearer)"],
                    ["OpenAI", "https://api.openai.com/v1",
                     "gpt-4o-mini, gpt-4o", "Required (Bearer)"],
                    ["NVIDIA NIM", "https://integrate.api.nvidia.com/v1",
                     "meta/llama-3.3-70b-instruct", "Required (Bearer)"],
                    ["LocalAI (local)", "http://127.0.0.1:8080/v1",
                     "—", "Not required"],
                    ["Custom (your own server)", "you set it yourself",
                     "—", "Optional"],
                ]),
            h.p("Below the list an orange privacy note about the selected "
                "provider appears — for example, “Local model — data never leaves this machine” "
                "or “Requests go to the Groq cloud”. Read it: cloud providers "
                "send the transcribed conversation text to a third-party server."),
            h.callout("warn", "Privacy",
                      "For confidential calls choose local Ollama (or LM "
                      "Studio/LocalAI): no key is needed, and the text goes nowhere. "
                      "Cloud services are faster and smarter, but the conversation is processed "
                      "by someone else's server — this breaks privacy."),
            h.p("To the right of the provider list there are three small buttons: “＋” — create "
                "your own provider profile, “⧉” — duplicate the selected one (handy for "
                "making your own variant of a built-in one), “✕” — delete your profile "
                "(the button is visible only on profiles you created yourself)."),

            h.p("For providers with the “OpenAI chat/completions” protocol, a “Reasoning "
                "effort” drop-down list appears below the list — it sets the “reasoning” field in the request to the model. "
                "Options: “(from profile)” (the default) — take the value from the provider "
                "profile; “(do not send)” — never send the field at all (for gateways and "
                "services that reject requests with an unknown parameter); "
                "low / medium / high — send that value. For reasoning models "
                "(deepseek, the OpenAI o-series, qwq) “low” noticeably speeds up the answer at the cost of "
                "reasoning depth, “high” — better quality but slower. "
                "If in doubt, leave “(from profile)”: ordinary models simply "
                "ignore this parameter. Note: the value chosen on "
                "a built-in provider is a global override: it takes priority "
                "over the effort setting of your own (custom) profiles too — "
                "if the effort “is not applied” on your own provider, set "
                "“(from profile)” back here."),

            h.h3("Your own provider"),
            h.p("Click “＋” or select “Custom (your own server)” — below the list "
                "a “Custom provider” panel opens with fields: name, BaseUrl (up to /v1), "
                "protocol (OpenAI chat/completions, OpenAI responses, Anthropic "
                "messages), authorization method (Bearer (Authorization), x-api-key or "
                "“No key”), additional HTTP headers (for gateways; in a value "
                "you can specify {session} — the session id will be substituted), model presets "
                "comma-separated and your own privacy note. The “Save” button writes "
                "the profile to the config, “Cancel” closes the panel. That is enough to "
                "connect a corporate gateway or an exotic service."),

            h.h2("Provider discovery on this machine"),
            h.p("At the bottom of the “API” tab there is a “Local provider discovery” block. "
                "It answers the question “can I connect using what is already "
                "on this computer?” — without copying keys by hand. Click "
                "“Scan”: the program checks the typical places where authorization "
                "usually sits on this machine and shows the result for each "
                "source on its own line — as the check progresses, not in one batch at "
                "the end, so you can see the scanner is working."),
            h.p("What exactly is checked:"),
            h.ul([
                "Codex (ChatGPT subscription) — the Codex authorization file, if present.",
                "OpenCode Go — the key from opencode settings.",
                "OpenAI API, Google Gemini API, Groq, OpenRouter, NVIDIA NIM — "
                "keys from environment variables.",
                "Gemini CLI, Devin CLI, Cursor, Factory Droid, Antigravity — "
                "clients installed on the computer.",
                "LM Studio (local) — whether the local model server is running; "
                "no key is needed for it.",
            ]),
            h.p("A line's state reads at a glance: “found” (in green), “not "
                "found”, “not supported” (the program is found but we cannot speak its protocol "
                "yet), “error” (the file or key is there but could not be read) "
                "and “pending”/“checking…” during the scan itself. "
                "A summary goes into the line, the details (what exactly was looked for and what was found) into "
                "the journal: the logs window, Ctrl+Shift+L."),
            h.callout("info", "Keys are only read",
                      "Scanning writes nothing to the settings: a found key "
                      "reaches the config only after you click "
                      "“Connect” on the source's line. Nothing found — "
                      "the section is unchanged, you can connect the old way, manually."),
            h.p("The “Connect” button on a found source's line itself creates "
                "a provider profile, fills in the address, key and model and saves the "
                "settings — after that the line shows “connected”. Local "
                "providers (LM Studio) connect without a key. Next to it there is a "
                "“Models” button: it asks the provider for the current model list "
                "and shows it in a drop-down field; pick a model and click "
                "“Apply model” — it becomes the active one for chat, the summary and "
                "the rest of the model requests. If the provider does not return a model list, "
                "the program states the reason right under the field — in that "
                "case the model ID is entered manually (see “Connection fields”)."),


            h.h2("Connection fields"),
            h.ul([
                "BaseUrl — the base API address strictly in the form https://host/v1, without "
                "the /chat/completions or /responses endings: the program itself adds "
                "the needed path. Examples are shown right under the field.",
                "Provider key — the field under BaseUrl, without a separate label. Needed "
                "only for cloud services; for local Ollama and LM Studio "
                "leave it empty. By default the key is shown in plain text — the button "
                "with the eye icon at the end of the field toggles showing/masking (handy when "
                "there are people behind your back or a screen share is running).",
                "Chat Model — the model's identifier at the provider; you can type it "
                "manually or pick it from a list. The “↻” button on the right asks the "
                "provider for the current model list (GET /models) — after loading, "
                "“✓ Models: N — choose from the list” appears under the field. If the list "
                "is unavailable, the program says so — just type the model ID from "
                "the model's page on the provider's website.",
                "RateLimit between LLM requests (sec) — the minimum pause between "
                "requests to the model. 0 by default (no pause). If the provider on "
                "a free tier answers with error 429, put 10–15 seconds here.",
            ]),
            h.p("The key is taken from the provider's personal account: platform.openai.com for "
                "OpenAI, console.groq.com for Groq, openrouter.ai for OpenRouter, "
                "build.nvidia.com for NVIDIA NIM. After entering the key and the model, click "
                "“Check connection” and then “Save” at the bottom of the window."),

            h.h2("The “Check connection” button"),
            h.p("The check runs step by step, and a report line is shown next to the button — "
                "from it you can see at which stage things broke:"),
            h.ol([
                "URL — the BaseUrl format is correct (http/https, no extra paths).",
                "Auth — the key has been applied to the request headers (for local "
                "providers the step passes as “No authorization”).",
                "Models — a request for the provider's model list; on success it shows "
                "“OK — models in the list: N”.",
                "Chat — a live ping: the program asks the model to answer with the single word "
                "“ready”. For the responses/anthropic protocols the step is skipped — that "
                "is normal, an ℹ mark will sit next to it.",
            ]),
            h.p("All steps “✓” — a green report, the link works. If some steps are "
                "red, read the error text on the right: the program translates provider codes "
                "into human language and suggests what to fix."),
            h.callout("info", "If “Check connection” complains",
                      "“API key is empty” or 401 — verify the key and the model ID "
                      "(an extra prefix like opencode-zen/… in the model name also causes "
                      "401). 402 — the provider's money/quota ran out. 404 — an error in "
                      "BaseUrl or the model. 429 — rate limit: wait or "
                      "set RateLimit. “No connection” — check the internet, the address and that "
                      "the local server (Ollama/LM Studio) is actually running."),
            h.p("A pitfall: when you switch providers, the program swaps the model to the "
                "new provider's preset — this is intentional, because a model ID of one "
                "service does not work at another. If instead of the substitution the model field "
                "was cleared, type the ID manually — there are examples under the field."),
            h.p("The details of every check run are written to the journal: the logs window "
                "(Ctrl+Shift+L) or the %APPDATA%\\BrainstormBuddy\\logs\\app.log file — "
                "look for lines tagged “CheckLlm”. The session log of LLM requests and "
                "responses is visible on the “Diagnostics” tab."),
        ])),

        ("The “LLM” tab — prompts and assistant behavior", "".join([
            h.p("If the “API” tab is about “where to send”, then “LLM” is about “how "
                "to think”. Here you choose and edit the system prompt — the "
                "instruction that defines the assistant's role: a teleprompter at an "
                "interview, a meeting secretary, a translator. Plus two parameters: "
                "merging the audio channels and the context size."),
            h.img("04-tab-llm.png", "Settings window, “LLM” tab: prompt presets and the editor", "wide"),

            h.h2("Prompt presets"),
            h.p("On the left is the “Prompt presets” list. The selected preset becomes "
                "the active system prompt (the same list is available right in the overlay, "
                "at the top left — the prompt can be switched on the fly). Out of the box "
                "these are installed:"),
            h.ul([
                "Interview: cheat sheet — ready-made first-person answers to the interviewer's questions;",
                "Meeting: analytics — risks, alternatives, wording for objections;",
                "Brainstorm — three non-obvious developments for every idea;",
                "Meeting minutes — brief talking points for the minutes: decision, task, "
                "owner, deadline;",
                "Sales: handling objections — answers to the client using the "
                "“join → argument → question” technique;",
                "Translation RU-EN — live translation of utterances in both directions;",
                "Technical consultant — answers of a senior engineer: commands, patterns, "
                "definitions;",
                "Learning (lecture) — listener mode: the assistant stays silent during the teacher's "
                "monologue and answers only explicit questions, while keeping "
                "notes and a glossary of terms.",
            ]),
            h.p("The presets use channel tags: [Speaker] — the other parties' speech "
                "from system audio, [Microphone] — your speech, [secret] — a message "
                "typed manually into the overlay input field. The prompt explains to the model whose "
                "text is in front of it and what to do with it."),

            h.h2("The prompt editor"),
            h.p("On the right — the “Preset name” field and a multi-line text editor. Edits "
                "in the editor immediately become the text of the selected preset. The buttons "
                "at the bottom:"),
            h.ul([
                "“Save as new” — saves the current text under the name from the "
                "“Preset name” field (if the name matches an existing one — it updates it);",
                "“Delete preset” — deletes the selected preset;",
                "“+ New empty” — creates an empty preset “New preset HH:MM” so that you "
                "can write from scratch.",
            ]),
            h.p("Tip: make a copy of a built-in preset (“+ New empty” or "
                "“Save as new”) and experiment on the copy — that way it is easier "
                "to return to the working variant. A prompt is plain text: write in Russian, "
                "who the model should impersonate, what answer format is needed (for example, “1–2 "
                "sentences”), and what is forbidden. Do not forget “Save” at the bottom of the window."),

            h.h2("History and channel merging"),
            h.p("The “Merge [Speaker] and [Microphone] into one LLM request” checkbox "
                "controls how utterances are assembled into a request: on — the other parties' utterances and yours go to the model as one message; off "
                "(the default) — as separately tagged parts. For dialogues "
                "(interview, sales) separate tagging is usually clearer to the model."),
            h.p("The “Chat history size (messages)” slider (2–20, default 15) — "
                "how many of the latest “question–answer” pairs are passed to the model as "
                "context. This is NOT a limit on the program's memory: the minutes and the export "
                "record the entire history, and the slider only sets how much of it the model "
                "sees. Less history — cheaper and faster requests; more — the model "
                "remembers the thread of the conversation better but answers are longer."),
            h.p("The “Response streaming (letters appear at once)” checkbox enables "
                "streaming mode (SSE): the suggestion is typed into the bubble and the "
                "karaoke strip as it is generated, rather than appearing in full after "
                "the wait. It works with providers that support the stream mode of the "
                "OpenAI protocol; if a provider ignores it, the program "
                "automatically reads the answer as ordinary whole-response JSON — so enabling it "
                "is safe on any provider. On by default "
                "(since version 2.18.0): if your provider's answers have become "
                "much longer or arrive slower, clear the box — "
                "some gateways in SSE mode ignore the token limit "
                "(max_tokens)."),

            h.p("The “Max response tokens” field (50–4096, default 400) next to the "
                "history slider limits the length of the model's answer — so that "
                "suggestions stay short. For reasoning models (deepseek, "
                "the o-series, qwq) set 600–1000: they spend most of the limit "
                "on hidden reasoning, and at 180 the answer comes back empty. If "
                "answers are cut off mid-sentence (the program catches "
                "finish_reason=length) — increase the value."),

            h.h2("How the program talks to the LLM"),
            h.p("Every request to the model runs through built-in safeguards "
                "(configured in the %APPDATA%\\BrainstormBuddy\\config.json file; there is no UI for them): the timeout of a single request is 30 seconds "
                "(RequestTimeoutSeconds), on 429 and 5xx responses up to 2 "
                "retries are made with a growing pause (MaxRetries, backoff 1→2→4 s). "
                "Connection errors (“server unreachable”) are not worn out with retries — "
                "the program immediately shows the “No connection to the server” status. Answer length "
                "is limited by the “Max response tokens” setting "
                "(MaxResponseTokens, 400 by default — see above), so that suggestions "
                "stay short."),
            h.callout("info", "Typical LLM errors",
                      "401 — “Invalid key, the model is not supported by this provider…” → "
                      "check the key and the model ID on the “API” tab. 402 — the balance "
                      "is exhausted (the program will shorten the request itself and retry once; "
                      "if that does not help — top up the account or change the model). 429 — "
                      "rate limit: after the retries set RateLimit on the "
                      "“API” tab. The “context_length_exceeded” error — the answer did not fit into "
                      "the context: the program will trim the history to 2 messages itself and "
                      "retry the request; you will barely notice it."),
            h.p("An empty model response (HTTP 200 with no text — this happens with "
                "reasoning models that “ate” the limit on reasoning) is "
                "also handled: the program automatically retries the request with a larger "
                "token limit. If no text appeared after that — see "
                "the “Diagnostics” tab (it holds the session log of →LLM/←LLM exchanges) and "
                "the %APPDATA%\\BrainstormBuddy\\logs\\app.log log."),
        ])),
    ]
