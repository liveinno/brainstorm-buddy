# -*- coding: utf-8 -*-
# "Getting started" chapters for the BrainstormBuddy User Guide:
# installation/first launch, how the overlay works, a live session.
# Screenshots in this fragment: 20-installer-lang, 21-installer-welcome,
# 01-overlay-idle, 02-overlay-answer (the last one is optional — see comment).


def chapters(h):
    return [
        (
            "Installation and first launch",
            "".join(
                [
                    h.h2("What gets installed"),
                    h.p(
                        "BrainstormBuddy is an assistant for live calls: it listens to the "
                        "meeting audio (the speakers and your microphone), recognizes speech, "
                        "sends questions to a language model and shows suggestions in a "
                        "semi-transparent overlay window on top of all other windows. The "
                        "overlay is excluded from screen capture, so the people you talk to "
                        "in Zoom, Teams or OBS cannot see it."
                    ),
                    h.p(
                        "The installer is a single BrainstormBuddy-Setup-*.exe file built on "
                        "Inno Setup. Everything needed (including the .NET runtime) is inside: "
                        "no additional components have to be installed."
                    ),
                    h.callout(
                        "info",
                        "System requirements",
                        "Windows 10 or newer, 64-bit only. The overlay's capture-invisibility "
                        "mode during screen sharing works starting with Windows 10 2004 "
                        "(build 19041). On earlier builds the program will run, "
                        "but the overlay will be visible in screen recordings.",
                    ),
                    h.h2("Which build to choose: Lite or Full"),
                    h.p(
                        "The installer ships in two variants — the name tells you which is "
                        "which: BrainstormBuddy-Setup-Lite.exe and "
                        "BrainstormBuddy-Setup-Full.exe. Both builds include the local "
                        "GigaAM speech recognition model (~1 GB): Russian speech "
                        "is recognized offline, without internet, right from the first launch."
                    ),
                    h.table(
                        ["Build", "Contents", "When to pick it"],
                        [
                            [
                                "Lite (~0.8 GB)",
                                "The app + GigaAM v3+v2 models. The Whisper model (~575 MB) "
                                "is not included — it will be downloaded from within the app "
                                "the first time you run file transcription with the Whisper engine.",
                                "The sensible default: Russian and mixed "
                                "meetings, fast recognition.",
                            ],
                            [
                                "Full (~1.6 GB)",
                                "Everything from Lite + the offline Whisper large-v3-turbo "
                                "model bundled.",
                                "When you need high-quality file transcription (including "
                                "English recordings) without extra downloads and without "
                                "internet access at install time.",
                            ],
                        ],
                    ),
                    h.callout(
                        "warn",
                        "The first installer window may take 1–2 minutes to appear",
                        "Antivirus and SmartScreen are checking an unsigned file of almost "
                        "a gigabyte in size. Do not run the installer again — "
                        "a second wizard will not open: the installer is protected against "
                        "parallel copies. Just wait for the window to appear.",
                    ),
                    h.h2("Step-by-step installation"),
                    h.ol(
                        [
                            "Run BrainstormBuddy-Setup-*.exe. In the first window "
                            "choose the wizard language — Russian or English — and click OK.",
                            "On the welcome page click Next and accept the license "
                            "agreement.",
                            "Choose who to install the program for: “just me” "
                            "(no administrator rights needed — the program will install to "
                            "%LOCALAPPDATA%\\Programs\\BrainstormBuddy) or “for all "
                            "users” (administrator required, the default folder is "
                            "Program Files).",
                            "On the component selection page leave “Full installation” "
                            "if you want the offline recognition model right away, or "
                            "“Compact” — then the GigaAM model (~80 MB) will be downloaded "
                            "on the app's first launch.",
                            "Optionally, on the additional tasks page enable "
                            "launch at Windows sign-in (off by default); "
                            "a desktop shortcut is created unless you clear that checkbox.",
                            "Click Install and wait for it to finish. On the last "
                            "page you can launch the program right away.",
                        ]
                    ),
                    h.img(
                        "20-installer-lang.png",
                        "Installation wizard: language selection",
                        "small",
                    ),
                    h.img(
                        "21-installer-welcome.png",
                        "Inno Setup installation wizard: welcome page",
                        "medium",
                    ),
                    h.p(
                        "Running the installer again is an update: the wizard will itself "
                        "offer to upgrade, reinstall or (if you try to install an older "
                        "version over a newer one) warn about a downgrade. During "
                        "an update a “Previous version settings” page appears: "
                        "by default the old configuration is reset, but a copy of it is kept "
                        "in Documents\\BrainstormBuddy — presets and resume records can be "
                        "recovered from there manually. A running instance is closed "
                        "automatically during the update."
                    ),
                    h.h2("First launch"),
                    h.p("After launching (from the wizard, the Start menu or the shortcut):"),
                    h.ul(
                        [
                            "a BrainstormBuddy icon appears in the system tray "
                            "(next to the clock) — the app lives there; there is no main window "
                            "on the taskbar;",
                            "a semi-transparent overlay opens on top of the windows — this is the main "
                            "working screen;",
                            "if no API key is set yet (and the LLM provider is not local), "
                            "the settings window opens automatically — the app needs "
                            "the address and key of a language model;",
                            "on the very first launch a short tutorial with golden highlights "
                            "runs on top of the settings and the overlay; "
                            "you can repeat it by running BrainstormBuddy.exe "
                            "with the --coach parameter;",
                            "the overlay status line first shows “⏳ Warming up "
                            "recognition…”, after a few seconds “✓ Recognition "
                            "ready — start speaking”, and then the permanent “Listening…”.",
                        ]
                    ),
                    h.callout(
                        "success",
                        "Minimum to get started",
                        "To get answers it is enough to pick an LLM provider on the "
                        "“API” tab of the settings (for a cloud provider — enter the key and model) "
                        "and click “Check connection”. Local speech recognition "
                        "works right away — it needs no setup.",
                    ),
                    h.img(
                        "10-tab-about.png",
                        "Settings, “About” tab: build version and a link to the app's website",
                        "medium",
                    ),
                    h.callout(
                        "info",
                        "Where things live",
                        "Settings: %APPDATA%\\BrainstormBuddy\\config.json · "
                        "Logs: %APPDATA%\\BrainstormBuddy\\logs\\app.log · "
                        "Models and the downloaded Whisper: %APPDATA%\\BrainstormBuddy\\models · "
                        "Transcript and minutes export: Documents\\BrainstormBuddy. "
                        "A second copy of the app will not start at the same time — launching it again "
                        "simply brings up the already running overlay.",
                    ),
                ]
            ),
        ),
        (
            "The overlay — the main window",
            "".join(
                [
                    h.p(
                        "The overlay is the only window you see during a meeting. It is always on top "
                        "of other windows, semi-transparent "
                        "(only the background is transparent — the text stays crisp), takes no space "
                        "on the taskbar and by default is invisible during screen sharing. "
                        "Size changes by grabbing the edges, position — by dragging the top bar "
                        "(free space next to the logo and the prompt list); the position is remembered "
                        "between launches."
                    ),
                    h.img(
                        "01-overlay-idle.png",
                        "The overlay at idle: prompt list on the left, buttons and the "
                        "“Listening…” status on the right",
                        "wide",
                    ),
                    h.h2("What is on screen"),
                    h.p(
                        "Top bar. On the left — the drop-down list of the active prompt "
                        "(the assistant's role; prompts are edited in the settings on the "
                        "“LLM” tab). On the right — two indicator chips (REC — audio recording, "
                        "LLM — sending to the model) and buttons: auto-scroll of the history, "
                        "settings (the gear, duplicates Ctrl+Shift+S), minimize, "
                        "maximize and hide the overlay (the cross)."
                    ),
                    h.p(
                        "The action pill in the header (between the prompt list and "
                        "the status): “Export transcript”, “AI meeting summary”, "
                        "“Screenshot mode” and “Clear history” (new chat — "
                        "the eraser button). A pressed button shows its own "
                        "“pressed” icon. Farther right — the “Listening…” status."
                    ),
                    h.p(
                        "The center of the window is the dialogue feed: recognized utterances — "
                        "bubbles on the right, assistant answers — cards on the left labeled “ANSWER”. "
                        "While the history is empty, a “Preparing speech recognition” placeholder sits there."
                    ),
                    h.p(
                        "At the bottom — the “Tokens … / clock” line, the “Send secret message "
                        "to LLM…” field (a text question to the assistant; Enter or the paper-plane "
                        "button sends it without voice) and the audio panel: "
                        "“microphone” — pause audio capture, “LLM off” — pure transcriber mode "
                        "(no answers), “speaker” — do not recognize system audio, a live sound-level wave "
                        "and the lightning “Send now” button — it sends the accumulated utterance immediately, "
                        "without waiting for a pause."
                    ),
                    h.table(
                        ["Element", "Tooltip on hover", "What it does"],
                        [
                            [
                                "Prompt list (header)",
                                "Active system prompt for the LLM",
                                "Switches the assistant's role; changing the prompt starts "
                                "a new dialogue context.",
                            ],
                            [
                                "REC and LLM chips",
                                "Audio recording / Sending to LLM",
                                "Flash while audio is being recorded and while a request to the model is in flight.",
                            ],
                            [
                                "Auto-scroll",
                                "Auto-scroll",
                                "The feed scrolls to new answers by itself; a yellow "
                                "icon means the mode is on.",
                            ],
                            [
                                "Export / summary / screenshot",
                                "Export transcript · AI meeting summary · "
                                "Screenshot mode (Ctrl+Shift+C)",
                                "Save the session text, generate the final minutes, "
                                "temporarily make the overlay visible for a screenshot.",
                            ],
                            [
                                "Eraser",
                                "Clear history (new chat)",
                                "Erases the feed and the dialogue context — the next answer "
                                "will not remember anything before it.",
                            ],
                            [
                                "Lightning (bottom right)",
                                "Send now",
                                "Sends an utterance that has been recorded but not yet recognized "
                                "for processing, without waiting for a pause.",
                            ],
                        ],
                    ),
                    h.p(
                        "Right-clicking any “question–answer” pair opens a menu: copying "
                        "(everything on screen, the full history, the question or the answer "
                        "separately), “Answer in detail” (the assistant will expand the last "
                        "answer into a detailed block under the card), “Focus on this pair” "
                        "(a green frame — to mark something important) and clearing the focus."
                    ),
                    h.p(
                        "The question and answer text is selectable right in the bubble — drag the "
                        "cursor over the fragment you need and copy it (Ctrl+C or right-click "
                        "→ “Copy”). Selection works when click-through mode is off "
                        "(the “ghost” button on the overlay bar)."
                    ),
                    h.callout(
                        "info",
                        "Engineer mode",
                        "The “Engineer mode” checkbox in the settings (“Overlay” tab) "
                        "adds the “SILENCE” and “CHUNK MAX” sliders to the screen, speech-rate "
                        "buttons and an audio equalizer for the last 2 minutes. "
                        "Off by default — normal work does not need it.",
                    ),
                    h.h2("Invisibility during screen sharing"),
                    h.p(
                        "The overlay is excluded from screen capture at the Windows level: during "
                        "screen sharing in Zoom, Teams, Discord or recording in OBS "
                        "you see the suggestions while the others do not. Works on "
                        "Windows 10 2004 and newer. The “Screenshot mode” button "
                        "(or Ctrl+Shift+C) temporarily disables the protection — so that you can "
                        "take a screenshot of the overlay yourself; the icon is highlighted "
                        "yellow while it is on."
                    ),
                    h.h2("Click-through mode"),
                    h.p(
                        "In the settings (“Overlay” → “Click-through mode”) you can make the window "
                        "transparent to the mouse: clicks will pass through the overlay into the windows "
                        "beneath it. Handy when the window sits over an important area. Note: "
                        "in this mode the overlay buttons cannot be clicked — turn it off "
                        "by double-clicking the left button on the overlay's top bar "
                        "(the title) or by unchecking the box in the settings again. Keyboard shortcuts "
                        "keep working regardless of the mode."
                    ),
                    h.p(
                        "Window opacity, screen position, font size, theme "
                        "(Dark Theme — dark, LightTheme — light) and the interface language "
                        "are configured on the “Overlay” tab in the settings. The default opacity "
                        "is 0.90; the Ctrl+Shift+O key cycles through it without "
                        "opening the settings."
                    ),
                ]
            ),
        ),
        (
            "A working session: how help during a meeting unfolds",
            "".join(
                [
                    h.h2("The “heard → recognized → answered” cycle"),
                    h.p(
                        "Set up the LLM once, pick a suitable prompt in the list "
                        "at the top left — and during the call there is nothing left to do. "
                        "The “Listening…” status means the app is recording audio and waiting for speech."
                    ),
                    h.ol(
                        [
                            "You speak (or the other party does — both the microphone and system "
                            "audio are captured). The speech detector cuts off a finished utterance at a pause — by default it targets "
                            "a pause of about 1.8 s and adapts to the speaker's pace.",
                            "The utterance goes to recognition — the REC chip lights up "
                            "for a couple of seconds. As soon as the text is ready, a bubble appears in the feed "
                            "with the question and a source tag: “[Speaker]” — the other party's voice "
                            "from speakers/headphones, “[Microphone]” — your own speech.",
                            "Under the bubble an “ANSWER (waiting for LLM "
                            "response…)” card appears — the text has already been sent to the model. A few seconds "
                            "later the answer replaces the placeholder, and the LLM chip "
                            "flashes at the moment of the request.",
                            "The status returns to “Listening…” — the cycle repeats. "
                            "Recognition and answers run in parallel: while the LLM is thinking about "
                            "one utterance, the next one is already being recognized.",
                        ]
                    ),
                    # OPTIONAL SCREEN (may be missing from the build — remove the line):
                    h.img(
                        "02-overlay-answer.png",
                        "The overlay at work: the recognized question on the right and the assistant's answer "
                        "on the left",
                        "wide",
                    ),
                    h.p(
                        "Every pair supports right-click actions — copy, ask for a detailed answer "
                        "or set a focus frame. At the bottom of the window "
                        "a counter of spent tokens and a session timer are kept."
                    ),
                    h.h2("Answer formatting"),
                    h.p(
                        "The model's answer is shown already formatted: headings, bold "
                        "and italic, bulleted and numbered lists, tables, "
                        "quotes, code blocks. Markup service characters (asterisks, "
                        "hashes, vertical bars) are not visible — there is no need to mentally strip them out."
                    ),
                    h.p(
                        "Links in the answer open on click (http, https and "
                        "mailto only). The answer can be selected with the mouse like ordinary text, together with "
                        "tables and code blocks: Ctrl+C "
                        "copies the selection, and if nothing is selected — the whole answer "
                        "as visible text without markup symbols; the “Copy” command in the answer's context menu "
                        "does the same (nearby — "
                        "“Select all”). In Meeting History the original text of the answer "
                        "is kept as is."
                    ),
                    h.p(
                        "The “Ask Memory” window supports the same selection and Ctrl+C, "
                        "plus a “Copy answer” button in the answer's header."
                    ),
                    h.h2("If an utterance did not appear"),
                    h.p(
                        "Not every sound becomes a bubble: pauses, noises and fragments "
                        "shorter than ~0.4 s are discarded before recognition, and an empty recognition "
                        "result is simply not shown. If there was speech but no bubble — check in order:"
                    ),
                    h.ul(
                        [
                            "Has the “live wave” gone dark and is the “Audio paused” status lit — maybe capture is paused (the microphone button "
                            "at the bottom or Ctrl+Shift+P).",
                            "Is “speaker off” enabled — then the other party's voice "
                            "is intentionally not recognized.",
                            "Speak a little longer and more clearly; check that "
                            "the settings (“Audio”) have the correct microphone "
                            "and output device selected.",
                            "Open the logs (Ctrl+Shift+L or the tray → “Show logs”): "
                            "lines of the Audio category show the signal level, Worker — "
                            "what recognition returned.",
                        ]
                    ),
                    h.h2("If there is a question but no answer"),
                    h.p(
                        "An error card will tell you the reason (for example, the provider's error "
                        "code or “the model returned an empty answer”), and the status line "
                        "may show “No connection to the server”. Typical causes: "
                        "no API key or a wrong one, the provider's balance/quota is exhausted, "
                        "the local Ollama is not running. The check — settings → “API” → "
                        "“Check connection”. If you do not need answers at all, enable "
                        "“LLM off” (the button at the bottom): the overlay becomes a pure "
                        "transcriber — just the speech text, no answer cards."
                    ),
                    h.h2("History and context"),
                    h.p(
                        "The assistant remembers the conversation: every next answer takes into account "
                        "the previous utterances and answers — the “Chat history size” setting on the “LLM” tab "
                        "sets the context size "
                        "(15 messages by default; older messages drop out of "
                        "the context but are not deleted — the whole feed goes into the export and the minutes). Changing the prompt in the list or the eraser button “Clear "
                        "history” starts a new dialogue from a clean slate."
                    ),
                    h.callout(
                        "warn",
                        "Pauses are not shutdowns",
                        "“Pause audio” (Ctrl+Shift+P) stops recording and "
                        "recognition; “Pause LLM” (Ctrl+Shift+Y) keeps recording but "
                        "does not send text to the model — bubbles labeled "
                        "“⏸ LLM paused” appear in the feed. Both modes are visible in the status line "
                        "and survive an app restart.",
                    ),
                    h.h2("A file with the question (attachment)"),
                    h.p(
                        "The paper-clip button next to the input field attaches a "
                        "document (PDF, DOCX, TXT etc.) or an image to the question — its text "
                        "is extracted and mixed into the answer context. For PDFs the "
                        "file dialog has a reading mode: “Fast” by default "
                        "(text layer only, local parsing — faster and without extra "
                        "requests to the model); “Full” is needed when OCR or "
                        "a per-page summary is required. If the document is longer than the OCR limit, "
                        "the tail is not lost — the remaining pages are read through the "
                        "text path."
                    ),
                    h.p(
                        "The “Extract facts” checkbox in the same dialog sends the document "
                        "to background memory extraction: progress is visible in the Memory Center "
                        "(a live percentage and a “Stop” button). A stopped "
                        "job shows the result “processed N of M parts, "
                        "K facts saved” and a “Continue” button; a failed one — "
                        "“Retry”; resuming picks up where it stopped, without "
                        "re-processing the finished parts."
                    ),
                    h.h2("Keyboard shortcuts (quick reference)"),
                    h.table(
                        ["Shortcut", "Action"],
                        [
                            ["Ctrl+Shift+H", "Show/hide the overlay"],
                            ["Ctrl+Shift+O", "Window opacity (cycles)"],
                            ["Ctrl+Shift+S", "Open settings"],
                            ["Ctrl+Shift+P", "Pause/resume audio"],
                            ["Ctrl+Shift+K", "Karaoke teleprompter on/off"],
                            ["Ctrl+Shift+Y", "Pause sending to the LLM"],
                            ["Ctrl+Shift+C", "Screenshot mode (show the overlay in capture)"],
                            ["Ctrl+Shift+L", "Logs window"],
                        ],
                    ),
                    h.p(
                        "The first five shortcuts can be remapped in the settings "
                        "(“Keyboard shortcuts”), details in a separate chapter. All "
                        "hotkeys are global: they fire from any window, even when "
                        "the overlay is hidden or in click-through mode."
                    ),
                    h.callout(
                        "info",
                        "Where to find session records",
                        "The on-screen feed is not everything: the “Export transcript” button "
                        "saves the text, “AI meeting summary” builds the final meeting summary, "
                        "and %APPDATA%\\BrainstormBuddy\\logs\\app.log keeps "
                        "a technical trace of every step (chunk → recognition → "
                        "LLM request → answer).",
                    ),
                ]
            ),
        ),
    ]
