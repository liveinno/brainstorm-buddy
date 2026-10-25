# -*- coding: utf-8 -*-
"""User guide fragment: the Resume, Agents and Diagnostics tabs, the Logs
window and a walkthrough of common problems. Screenshots: 11-tab-summary.png,
12-tab-agents.png, 13-tab-diag.png, 15-log-window.png."""


def chapters(h):
    return [
        # ==================== 1. Resume tab ====================
        (
            "The Resume tab: who you are to the assistant",
            "".join([
                h.p("The Resume tab is your profile: who you are, what you can do "
                    "and what you have achieved. The assistant injects this text "
                    "into the agents' prompts so that answers rely on real facts "
                    "about you instead of the model's inventions. In an interview "
                    "this is the difference between “a relevant answer with your "
                    "own numbers” and “a polished but borrowed story”."),
                h.callout(
                    "info",
                    "This is not meeting minutes",
                    "Despite the name, this tab is not about meeting outcomes. The "
                    "final minutes are produced separately — with the “AI meeting "
                    "summary” button in the overlay. What you set here is your "
                    "“candidate card”."),
                h.img("11-tab-summary.png",
                      "The Settings window, Resume tab: the profile text and the "
                      "“can't do” list",
                      "wide"),
                h.h2("What is configured here"),
                h.ul([
                    "“Brief summary” — a large text field. Write facts, figures and "
                    "project names: the assistant takes data strictly from here and "
                    "invents nothing on top.",
                    "“Can't do / avoid” — topics you have not touched or do not want "
                    "to develop. If a question drifts into this zone, the assistant "
                    "will not stretch to force-fit an unrelated project.",
                    "“Save profile” — applies the text to the current configuration. "
                    "For the changes to be written to config.json and survive a "
                    "restart, press the general “Save” button at the bottom of the "
                    "window afterwards.",
                ]),
                h.h2("How it works in answers"),
                h.p("The profile text is substituted into the agents' system prompts "
                    "in place of the {{USER_PROFILE}} placeholder. All built-in "
                    "agents of the multi-agent scenarios (the Agents tab) already "
                    "contain this placeholder — fill in the resume once and every "
                    "agent will see your cases, skills and “can't do” zones."),
                h.p("Note: in single-assistant mode (when multi-agents are off) the "
                    "profile is not injected into the prompt automatically — single "
                    "presets from the LLM tab work from their own text."),
                h.callout(
                    "warn",
                    "Replace the demo resume",
                    "In a fresh installation the fields are filled with a fictional "
                    "“IT project manager” example — it is a format sample, not a "
                    "template made for you. Until you enter your own data, the "
                    "agents will answer with facts from somebody else's career."),
            ]),
        ),

        # ==================== 2. Agents tab ====================
        (
            "The Agents tab: multi-agent mode",
            "".join([
                h.p("By default BrainstormBuddy works as a single assistant: the "
                    "interlocutor's line goes to the LLM with the active prompt "
                    "preset, and you get one answer. The Agents tab enables a "
                    "different mode — several “roles” look at the dialogue at once, "
                    "and each of them answers only within its own topic."),
                h.img("12-tab-agents.png",
                      "The Settings window, Agents tab: the toggle, the scenario "
                      "and the role list",
                      "wide"),
                h.h2("What is configured here"),
                h.ul([
                    "“Enable multi-agents” — the main toggle. While it is off, a "
                    "single assistant with the prompt from the overlay is active.",
                    "“Scenario” — a drop-down list of ready-made role sets.",
                    "The scenario's agent list — cards with names; the checkbox "
                    "next to a name enables or disables that specific role.",
                ]),
                h.h2("Built-in scenarios"),
                h.table(
                    ["Scenario", "Roles inside", "What it is for"],
                    [
                        ["Interview",
                         "TechLead, HRD + Defense",
                         "TechLead gives ready answers to technical and project "
                         "questions (STAR format, facts from your resume). HRD stays "
                         "silent on technical topics and answers HR questions: "
                         "motivation, weaknesses, money, plus the “Defense” block "
                         "under pressure."],
                        ["Brainstorm with analysts",
                         "Idea generator, Critic, Moderator",
                         "Ideas and options, finding weak spots and risks, "
                         "structuring the discussion."],
                        ["Client call",
                         "Product Manager, Customer Advocate, Tech Estimator",
                         "Requirements and user stories, defending the client's "
                         "interests, estimating technical complexity."],
                        ["Career consultation",
                         "Career coach, Skills analyst, Market",
                         "Career direction, skill gaps, demand and trends."],
                        ["1-on-1 with a team lead",
                         "Active listener, Goal coaching, Advocate",
                         "Help formulating thoughts, goals and OKRs, arguing "
                         "your position."],
                        ["Learning",
                         "Q&A, Glossary, Notes",
                         "Lectures and training: stays silent during the "
                         "instructor's monologue, answers only explicit questions, "
                         "and keeps notes and a term glossary on the side."],
                    ]),
                h.h2("How it looks in the overlay"),
                h.p("Agents answer only the interlocutor's questions — lines that "
                    "contain a “?” or question words. Your own speech (the "
                    "“[Microphone]” marker) and fragments of phrases get no "
                    "comment: the overlay shows the note “🎧 your line — added to "
                    "context”, and the text goes into the rolling context of the "
                    "last six lines that all agents share."),
                h.p("Each agent may stay silent if the question is outside its "
                    "topic. If everyone stays silent, the bubble shows "
                    "“(agents stayed silent)” — that is normal. In the Interview "
                    "scenario the answer is split into blocks: the main line from "
                    "TechLead, an “HR ADVICE” card and a “Defense” card on how to "
                    "handle manipulation and stress questions."),
                h.callout(
                    "warn",
                    "Multi-agents = more requests",
                    "Every question from the interlocutor costs one request per "
                    "enabled agent. That burns tokens faster and hits the limits "
                    "of free API tiers sooner. On tight plans keep only the one or "
                    "two most useful roles."),
                h.h2("Fine-tuning"),
                h.p("Role texts are edited not in the window but in the "
                    "configuration file: “Open folder” at the bottom of the "
                    "settings window → config.json → the “MultiAgent” section → "
                    "“Scenarios”. Each agent has SystemPrompt, MaxWords, Tone, "
                    "Style, Language. Inside SystemPrompt these placeholders work: "
                    "{{USER_PROFILE}} (your resume), {{MAX_WORDS}}, {{STYLE}}, "
                    "{{TONE}}, {{LANGUAGE}}."),
                h.callout(
                    "info",
                    "Where to see what the agents answered",
                    "Every request and response of every agent — with the role "
                    "name, time and verdict — is written to the session “LLM log” "
                    "on the Diagnostics tab, and agent errors go to the general "
                    "log with the Agent category."),
            ]),
        ),

        # ==================== 3. Diagnostics tab and the logs window ====================
        (
            "The Diagnostics tab and the Logs window",
            "".join([
                h.p("When something goes wrong, the first answer to “why?” lives "
                    "here. The Diagnostics tab collects everything you need for "
                    "self-checks and for talking to support: anonymized archive "
                    "collection, log detail toggles and a live log of LLM "
                    "requests."),
                h.img("13-tab-diag.png",
                      "The Settings window, Diagnostics tab: log collection, "
                      "flags and the LLM log",
                      "wide"),
                h.h2("The “Logs for support” block"),
                h.p("The “Collect logs for support” button packs a zip archive "
                    "(into “Documents\\BrainstormBuddy”, which opens "
                    "automatically): system and active-engine details, settings "
                    "with keys and tokens cut out, anonymized log files and an "
                    "errors.txt error summary. If there were crashes, the archive "
                    "also gets crash.log (the tail of the “silent” crash journal, "
                    "up to 200 lines, also anonymized) and crash-last.txt — a "
                    "marker of the last abnormal termination. Question-answer "
                    "history and transcriptions do not go into the archive — you "
                    "can send it without disclosing meeting content. The “Open "
                    "logs folder” button leads to where the files live."),
                h.callout(
                    "info",
                    "WER dumps are not in the archive — send them separately",
                    "Full native crash dumps (*.dmp, hundreds of megabytes) are "
                    "not put into the zip — the README.txt file in the archive "
                    "only lists the dumps found (name, date, size) and the folders "
                    "where they live: %APPDATA%\\BrainstormBuddy\\dumps and "
                    "%LOCALAPPDATA%\\CrashDumps (only BrainstormBuddy dumps "
                    "count). If a crash repeats — send a fresh .dmp separately: "
                    "from it support can see exactly what failed."),
                h.h2("Logging settings"),
                h.ul([
                    "“Write log file” — on by default; turning it off completely "
                    "stops app.log being written to disk.",
                    "“Verbose log (debug)” — enables the Debug level: VAD "
                    "transitions, request bodies, audio levels. Keep it off in "
                    "normal use: more noise, and fragments of recognized speech "
                    "may end up in the file.",
                    "“Files to keep (rotation)” — how many app.log files to keep "
                    "(5 by default). A single file is capped at about 1 MB, after "
                    "which it is renamed to app.1.log, app.2.log and so on.",
                ]),
                h.h2("The current-session LLM log"),
                h.p("The lower half of the tab is a live feed of model calls: each "
                    "“request → response” pair is a line with time, model and "
                    "duration (in multi-agent mode — one entry per agent with the "
                    "role name and status). Newest entries on top, the last 200 "
                    "pairs are kept. The “Clear” and “Copy” buttons sit to the "
                    "right above the field. The log is session-scoped: it is "
                    "cleared when the app restarts."),
                h.h2("The “Logs” window"),
                h.p("A separate window for live watching: Ctrl+Shift+L or the "
                    "tray icon → “Show logs”. It shows the last 500 events of the "
                    "internal buffer (rows like “time | level | category | "
                    "message”) and picks up new ones in real time."),
                h.img("15-log-window.png",
                      "The “Logs” window: level filter, auto-scroll, colored "
                      "entries",
                      "wide"),
                h.ul([
                    "“Level” — the filter: “All”, “Info+”, “Warn+”, “Errors only”. "
                    "Changing the filter rebuilds the list from the buffer.",
                    "“Auto-scroll” — the list follows the tail; uncheck it to read "
                    "the middle in peace.",
                    "“Pause” — stops adding new events to the list (they are "
                    "still written to the file as usual).",
                    "“Clear” — clears only the visible list; the buffer and the "
                    "file are untouched.",
                    "“Copy” — puts the whole event buffer on the clipboard as "
                    "text, handy to attach to a support email.",
                    "“Open folder” — opens the logs directory in Explorer.",
                ]),
                h.p("Rows are colored by level: Debug — gray, Info — blue, Warn — "
                    "yellow, Error — red and bold. The category in square "
                    "brackets hints at the subsystem: Audio (sound), Ai and "
                    "Answer (LLM requests), Stt and LocalStt (recognition), UI, "
                    "Loop, Agent, Config, Diag."),
                h.callout(
                    "info",
                    "Where the log files live",
                    "%AppData%\\BrainstormBuddy\\logs\\app.log is the current "
                    "file, with archived app.1.log…app.4.log next to it. The exact "
                    "path is printed on the About tab, which also has the “Open "
                    "logs folder” button. If the primary directory suddenly "
                    "becomes unwritable, the app switches to "
                    "%TEMP%\\BrainstormBuddy\\logs after three failed attempts and "
                    "leaves a note about it in the log itself."),
            ]),
        ),

        # ==================== 3.5. History tab ====================
        (
            "The History tab and the meeting journal",
            "".join([
                h.p("BrainstormBuddy continuously writes the meeting to disk from "
                    "the very start: every recognized line of the interlocutor, "
                    "your own line and every assistant answer is appended to the "
                    "journal file as a separate row. Writing uses an "
                    "open-append-close pattern with a forced flush to disk — so "
                    "the journal survives a hang, a killed process and even a "
                    "sudden PC reboot. You do not need to press anything: history "
                    "is saved by itself."),
                h.p("Each meeting is a separate file "
                    "%AppData%\\BrainstormBuddy\\meetings\\"
                    "yyyy-mm-dd_hh-mm-ss.jsonl. A new file is opened at app start "
                    "and after “Clear history (new chat)” in the overlay."),

                h.h2("Where to look"),
                h.p("The meeting list lives on the History tab in Settings (a "
                    "left-menu item between “Diagnostics” and “About”) — the same "
                    "place holds the “Open”, “Delete selected” and “Open folder” "
                    "buttons. The list supports multi-select (Ctrl/Shift+click), "
                    "“Select all” selects the whole list; bulk deletion asks for "
                    "confirmation and erases the meetings together with their "
                    "journal files. The full history window opens with a double "
                    "click on a meeting, with the “Open” button, or from the tray "
                    "— “Meeting history”."),
                h.img("36-tab-history.png",
                      "The History tab: the meeting list, search and journal "
                      "management",
                      "wide"),

                h.h2("The “Meeting history” window"),
                h.p("It works like “File transcription”: on the left the list of "
                    "meetings with date, duration and line count; on the right "
                    "the selected meeting. The transcript is formatted with "
                    "timestamps and roles (Interlocutor / You / AI). The buttons "
                    "above the text: “Copy all” to the clipboard, “Download "
                    ".txt” — export to a file, “Delete” — erases the meeting from "
                    "disk (also available in the list's context menu)."),
                h.img("37-meeting-history.png",
                      "The “Meeting history” window: the meeting list on the "
                      "left, the transcript of the selected meeting on the right",
                      "wide"),
                h.h2("AI meeting summary"),
                h.p("The bottom panel assembles the meeting minutes through the "
                    "current LLM provider: decisions, tasks, agreements. Long "
                    "transcripts are automatically split into chunks and reduced "
                    "map-reduce style (progress is visible in the window). The "
                    "finished summary is stored in the meeting's metadata and "
                    "shown the next time you open it; the “Copy all”/“Download "
                    ".txt” buttons work for it too."),
                h.callout(
                    "info",
                    "What gets written to the journal",
                    "The journal writes ALL lines regardless of the karaoke "
                    "visibility filters — it is the meeting's audio record in "
                    "text, not a copy of the screen. The demo preview feed of the "
                    "karaoke is not written to the journal. If a journal is "
                    "partially damaged, the history window skips broken rows and "
                    "shows the rest."),
            ]),
        ),

        # ==================== 4. Common problems ====================
        (
            "Common problems and what to do about them",
            "".join([
                h.p("We collected typical situations and quick checks. The general "
                    "order is: check the status in the overlay → open the logs "
                    "(Ctrl+Shift+L) → look for rows of the relevant category → "
                    "fix it using the tables below."),
                h.h2("Audio and recognition"),
                h.table(
                    ["Symptom", "Cause and what to do"],
                    [
                        ["The “Microphone unavailable” notification",
                         "The microphone is occupied by another program or "
                         "disabled in Windows. Close the apps holding the "
                         "microphone, check microphone access in Windows privacy "
                         "settings and pick the device again: Settings → Audio."],
                        ["The “Loopback device unavailable” notification, only your own voice is heard",
                         "System audio could not be captured — the app works with "
                         "the microphone only, the interlocutor's lines are not "
                         "recognized. Check that sound is output to the default "
                         "device (not an HDMI/monitor without speakers) and that "
                         "sound is not muted by the “speaker off” button in the "
                         "overlay."],
                        ["There is speech but nothing appears in the overlay",
                         "Lines shorter than ~0.4 seconds are discarded as clicks "
                         "and noise — speak in full phrases. Next: enable "
                         "“Verbose log” and watch the Audio/Worker categories — "
                         "are there VAD: speech start/end events and what RMS the "
                         "signal has. If RMS is below the threshold, lower the "
                         "sensitivity threshold on the Audio tab. If a line was "
                         "recognized but the answer is empty — that is an LLM "
                         "issue, see below."],
                        ["The first lines are recognized slowly",
                         "The recognition engine warms up at start: wait for the "
                         "“✓ Recognition ready — start speaking” status. If an "
                         "external STT server (Docker) is selected — the first "
                         "request starts the container; if the server is "
                         "unreachable the app falls back to the built-in engine "
                         "by itself and tells you with a notification."],
                    ]),
                h.h2("Assistant answers"),
                h.table(
                    ["Symptom", "Cause and what to do"],
                    [
                        ["The “No connection to server” status",
                         "The app could not reach the LLM BaseUrl: no internet, a "
                         "local server (Ollama etc.) is not running, or the "
                         "port/address is wrong. Check the connection and the "
                         "address on the API tab with the “Check connection” "
                         "button. The error is not fatal — the app keeps "
                         "listening."],
                        ["Error 401 in the answer bubble",
                         "“Wrong key, the model is not supported by the provider, "
                         "or an extra prefix in the model ID.” Compare the key on "
                         "the API tab and the model ID: remove prefixes like "
                         "“opencode-zen/…”, make sure the model exists at this "
                         "provider at all."],
                        ["Error 402 — “limit/balance exhausted”",
                         "Top up your balance at the provider or switch to another "
                         "model (Settings → API)."],
                        ["Error 429 — “rate limited”",
                         "The provider's rate limit. Wait, change the model/plan; "
                         "in multi-agent mode reduce the number of enabled roles — "
                         "every question generates a request per agent."],
                        ["“The model returned an empty answer”",
                         "A common picture with reasoning models: the whole budget "
                         "went into “thinking”. Increase the response token limit "
                         "or switch to a regular chat model."],
                        ["Answers take a long time",
                         "Watch the time in the LLM log on the Diagnostics tab: "
                         "the request and response rows show whether STT or the "
                         "model itself is slow. The first answer after launch is "
                         "usually slower: the model and the engine are warming up. "
                         "On free tiers check whether you are hitting the limits "
                         "(429)."],
                        ["“(agents stayed silent)” / “🎧 your line — added to context”",
                         "This is not an error but multi-agent logic: agents stay "
                         "silent on other roles' topics, and your own lines and "
                         "non-questions go only into the dialogue context. If "
                         "agents are silent on an explicit question — check that "
                         "the needed role is checked on the Agents tab."],
                    ]),
                h.h2("Window and visibility"),
                h.table(
                    ["Symptom", "Cause and what to do"],
                    [
                        ["The overlay is visible during screen sharing",
                         "Capture protection works on Windows 10 version 2004 and "
                         "newer. Check the OS version and, if needed, enable "
                         "“screenshot mode” (Ctrl+Shift+C): it hides the overlay "
                         "from recording and screen sharing, a second press "
                         "restores visibility."],
                        ["The overlay is lost / the window went off-screen",
                         "The overlay minimizes to the tray: bring it back with a "
                         "double click on the icon or Ctrl+Shift+H. If the window "
                         "was opened outside the screen, changing the position on "
                         "the Overlay tab helps."],
                    ]),
                h.h2("If nothing helped"),
                h.ol([
                    "Open the logs (Ctrl+Shift+L), set the “Warn+” filter and "
                    "reproduce the problem.",
                    "If the logs are empty — check that “Write log file” is "
                    "enabled on the Diagnostics tab; for a deep dive temporarily "
                    "enable “Verbose log” and repeat.",
                    "Press “Collect logs for support” on the Diagnostics tab — "
                    "the diagnostics_*.zip archive in “Documents\\BrainstormBuddy” "
                    "is already anonymized and ready to send to support.",
                ]),
            ]),
        ),
    ]
