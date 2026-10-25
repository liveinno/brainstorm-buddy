# -*- coding: utf-8 -*-
def chapters(h):
    return [
        ("The “Overlay” tab", "".join([
            h.p("The “Overlay” tab in the settings window (the “Overlay” item in the left menu) controls "
                "the look and behavior of the main hint window: how transparent it is, where it sits "
                "on the screen, how large the text is and which theme draws it all."),
            h.img("08-tab-overlay.png",
                  "The settings window, “Overlay” tab: opacity, position, checkboxes, font and theme",
                  "wide"),

            h.h2("What is configured here"),
            h.table(
                ["Setting", "What it does", "Default"],
                [
                    ["Opacity", "A slider from 0.2 (almost transparent) to 1.0 (opaque). "
                     "The current value is shown right under it — “Current: 0.90”.", "0.90"],
                    ["Screen position", "The corner of the screen where the overlay appears: TopRight, TopLeft, "
                     "BottomRight, BottomLeft or Center.", "TopRight"],
                    ["Show border (compatibility mode)", "Draws a thin black frame around the "
                     "window — useful if on a light background the overlay edges “get lost”.", "off"],
                    ["Click-through mode (clicks pass through the overlay)", "The window stops reacting "
                     "to the mouse: clicks go to the program beneath it.", "off"],
                    ["Show the technical line (rec / STT / LLM) on the answer",
                     "Every answer gets a line with the recording, recognition and model response "
                     "times.", "on"],
                    ["Show past-meeting sources under the answer",
                     "Links to past-meeting snippets are shown in one compact horizontal line "
                     "under the answer; a click opens the meeting at that snippet. Clearing the "
                     "checkbox hides the block entirely.", "on"],
                    ["Engineer mode", "The overlay gains pause and chunk sliders, speech-rate "
                     "presets and an equalizer. Off — the main screen stays clean.",
                     "off"],
                    ["Overlay font size (pt)", "A slider from 10 to 22 pt, step 1.",
                     "17"],
                    ["Theme", "Dark Theme is dark, LightTheme is light.",
                     "Dark Theme"],
                    ["Interface language / Language", "Russian or English.", "Russian"],
                    ["Hide tray icon", "Removes the icon from the notification area — bring it "
                     "back with a hotkey (Ctrl+Shift+T, configurable on the “Hotkeys” "
                     "tab).", "off"],
                    ["Stealth mode — process name in Task Manager", "An alias for the exe file "
                     "without “.exe”: on the next launch the app restarts as a copy of "
                     "%APPDATA%\\BrainstormBuddy\\stealth\\<name>.exe and Task Manager shows "
                     "that copy. Empty — normal launch.", "empty"],
                ]),
            h.p("At the very bottom there is the “Export folder” field. Files exported with the "
                "transcription and AI meeting minutes export buttons are saved here. Leave the "
                "field empty — files go to “Documents\\BrainstormBuddy”. The “…” button "
                "on the right opens the standard folder picker."),

            h.h2("What applies immediately and what needs “Save”"),
            h.p("Theme and language switch instantly: pick LightTheme and the window repaints in "
                "light tones without waiting for any button. The rest of the tab's settings "
                "(opacity, position, font, checkboxes) take effect after pressing "
                "“Save” at the bottom of the window — no restart needed, the overlay refreshes in place, "
                "and the program shows a “Configuration saved” toast."),
            h.callout("info", "Opacity without settings",
                      "Ctrl+Shift+O changes opacity right during a meeting: each press adds 20%, "
                      "and after 100% the cycle restarts from 40%. The current value is shown "
                      "with an “Opacity” toast."),
            h.callout("warn", "Click-through mode and the mouse",
                      "With “Click-through mode” enabled the overlay does not catch clicks — you will not be able "
                      "to press its buttons or pick a prompt with the mouse. Control stays on the "
                      "keyboard shortcuts: Ctrl+Shift+H hides and restores the window, and the window can be "
                      "dragged by its title bar (the mode is temporarily disabled while dragging "
                      "and re-enabled automatically)."),

            h.h2("Where the logs are"),
            h.p("Applying the settings is written to the common log %APPDATA%\\BrainstormBuddy\\logs\\app.log "
                "— the line “Overlay.ApplyUiConfig: opacity=…, position=…, fontSize=…” shows which values "
                "actually reached the window."),
        ])),
        ("The “Karaoke” tab — teleprompter", "".join([
            h.p("The “Karaoke” tab (the “Karaoke” item in the left settings menu) controls a "
                "second, separate hint window: a semi-transparent teleprompter strip that lies "
                "on top of all windows at the edge of the screen. Unlike the main overlay, it shows "
                "not the chat history but a live feed: lines fill the strip top-down from the very "
                "top, and when space runs out the feed smoothly scrolls up — the freshest utterance "
                "always stays at the lower edge. Older lines dim with age (configurable), the "
                "freshest one has an accent marker on the side, and your partner's utterances can be "
                "painted in a separate color. The strip is toggled with a single press of Ctrl+Shift+K (the "
                "shortcut is changed on the “Hotkeys” tab)."),
            h.img("17-tab-karaoke.png",
                  "Settings, the “Karaoke” tab: strip position and size, font, colors, "
                  "animations, utterance filters, stealth and click-through modes",
                  "wide"),
            h.img("18-karaoke-live.png",
                  "The live karaoke feed: the freshest utterance is highlighted gold, older "
                  "ones shift toward the main color and fade to gray (the illustration shows "
                  "black text without a background strip — text-only mode)",
                  "wide"),

            h.h2("Position and size"),
            h.table(
                ["Setting", "What it does", "Default"],
                [
                    ["Enable karaoke overlay", "Shows the strip on the screen. Off — "
                     "the window is not created at all and consumes nothing.", "off"],
                    ["Strip position", "Which edge of the screen to sit at: Bottom, Top, Left or "
                     "Right (side positions use a vertical strip).", "Bottom"],
                    ["Strip width (% of screen)", "A slider from 20 to 100%: how much of the screen width the strip "
                     "takes (for side positions — the width of the vertical strip).", "60"],
                    ["Strip height (% of screen, 0=fit content)", "A slider from 0 to 100%: at "
                     "1–100 the strip gets a fixed height as a fraction of the screen height "
                     "(for side positions — the height of the vertical strip). At 0 the height "
                     "is elastic — it hugs the number of lines and the font size.", "25"],
                    ["Strip inner padding (px)", "The margin around the text inside the strip, "
                     "0–80 px.", "24"],
                    ["Screen edge offset (px)", "The gap between the strip and the screen edge, "
                     "0–200 px.", "30"],
                    ["Window opacity", "The overall opacity of the strip window: from 0.25 (almost "
                     "transparent) to 1.0.", "0.90"],
                ]),

            h.h2("Text and colors"),
            h.table(
                ["Setting", "What it does", "Default"],
                [
                    ["Font size (pt)", "A slider from 12 to 64 pt — hints readable from a distance.", "14"],
                    ["Text alignment", "Left or Center.", "Left"],
                    ["Text anchor", "Works when “Strip height” > 0 (a fixed-height strip): where to pin a short feed — "
                     "Bottom (default, empty space on top) or Top (text at the upper edge, empty space below). "
                     "On overflow the feed scrolls up anyway and the fresh line stays at the lower edge.", "Bottom"],
                    ["Word wrap", "Long utterances wrap to a second line; off — they are cut at the strip width.", "on"],
                    ["Text color", "The main color of the lines in #AARRGGBB format "
                     "(alpha + RGB).", "#FFFFFFFF"],
                    ["Fresh-line marker color", "The accent color of the marker next to the newest "
                     "utterance.", "#FF4ADE80"],
                    ["Fresh line color", "The color of the newest utterance (#AARRGGBB); with the "
                     "age gradient enabled the next line takes the text color, and older ones smoothly fade "
                     "toward the old lines color.", "#FFFFE9A8"],
                    ["Old lines color", "The color old utterances fade to when the age gradient "
                     "is enabled (#AARRGGBB). Set white or your own hex code if the old lines are unreadable.", "#FFFFFFFF"],
                    ["Strip background color", "The fill of the strip rectangle, #AARRGGBB.",
                     "#FF0A1622"],
                    ["Strip background opacity", "From 0 (background invisible) to 1 (solid "
                     "backdrop).", "0.78"],
                    ["Background strip (uncheck — text only, no rectangle)",
                     "Uncheck it and the backing rectangle disappears entirely, leaving only "
                     "text on top of the windows; readability depends on what is behind it "
                     "(in this mode the text shadow helps).", "on"],
                    ["Text shadow (when background is hidden)", "A dark shadow on the letters — the text does not blend "
                     "into the windows behind the strip when the background strip is unchecked. With the "
                     "background on, no shadow is drawn.", "on"],
                    ["Interlocutor question color", "A separate color for “question” lines "
                     "(utterances from the speakers) — #AARRGGBB. The partner's question is always "
                     "painted in this color and does NOT follow the age fade, so it stays visible "
                     "against the answers.", "#FFF27A8A"],
                ]),

            h.h2("Feed behavior"),
            h.table(
                ["Setting", "What it does", "Default"],
                [
                    ["Visible lines", "How many lines to keep on screen at once, 1–30 "
                     "(at “Strip height = 100%” the feed stretches over the whole screen).",
                     "4"],
                    ["Line slide-in speed (ms)", "The duration of the new-line entrance animation, 100–2000 ms.", "700"],
                    ["Line fade-out speed (ms)", "The duration of the smooth fade, 100–2000 ms.", "600"],
                    ["History fade (old lines dim)", "Enables the age-based dimming of old lines. Clearing the checkbox "
                     "makes ALL lines equally bright (fade is fully disabled, including the top shadow mask).", "on"],
                    ["Min. opacity of old lines (0 — vanish, 1 — full)", "The lower bound of the fade, 0–1: "
                     "how pale the oldest lines can get. Set it higher (0.35–0.5) if the history is hard to read, or 1 — so that lines "
                     "never dim. Works only when “History fade (old lines dim)” is enabled.", "0.35"],
                    ["History lines above current", "How many previous utterances to keep above the "
                     "fresh line, 1–6. Limits the history depth independently of “Visible lines” — a tall window is no longer packed with ancient "
                     "semi-transparent lines.", "3"],
                    ["Color gradient by age", "The color also ages: the freshest utterance gets the "
                     "“Fresh line color”, the next one the text color, then a smooth transition to the "
                     "“Old lines color”.", "on"],
                    ["Marker on the fresh line", "An accent mark on the side of the last utterance — "
                     "the eye instantly finds what was just added.", "on"],
                ]),

            h.h2("What gets into the feed"),
            h.p("Three filter checkboxes decide which utterances are shown. By default only the AI's answers are enabled — the feed works as a pure hint teleprompter. "
                "“Show interviewer questions” adds utterances from the speakers, "
                "“Show my lines (microphone)” — your own; both are disabled "
                "by default. There is no separate color for your own lines: they are painted by the same "
                "rule as the answers (fresh — the “Fresh line color”, then the age gradient). Only the partner's questions get a special color — the "
                "“Interlocutor question color” from the table above, with no fading."),

            h.h2("Stealth and the mouse"),
            h.p("“Invisible to screen capture (only you see it)” marks the window with the "
                "WDA_EXCLUDEFROMCAPTURE flag: the strip is visible to you but does not get into a Zoom/Teams screen "
                "share, into OBS recordings or screenshots. “Click-through mode” "
                "lets mouse clicks pass through the strip — it does not interfere with working in the windows "
                "beneath. Both checkboxes are enabled by default."),
            h.callout("warn", "Stealth does not beat “picture in picture”",
                      "If you share your screen via a camera pointed at the monitor, or via hardware "
                      "capture below Windows — the strip will be visible. The flag works "
                      "only for software capture performed by the OS."),

            h.h2("The “Preview” button"),
            h.p("At the bottom of the tab the “Preview” button shows a demo feed with the current "
                "settings — handy for picking the font, colors and animation speeds without waiting for real "
                "utterances. The strip opens for the preview even when “Enable karaoke overlay” is unchecked."),
        ])),

        ("The “Screen OCR” tab — questions from the screen", "".join([
            h.p("The “Screen OCR” tab (the “Screen OCR” item in the left settings menu, between "
                "“Agents” and “Diagnostics”) makes the assistant read text straight "
                "from the screen: every few seconds the selected monitor is captured, an OCR engine "
                "(Tesseract or the system Windows OCR — chosen on this same tab) "
                "recognizes Russian and English text on it, and a found question goes "
                "to the LLM. The answer is shown in the overlay and in the karaoke feed just like the answer "
                "to a partner's utterance — handy when the question is visible on the screen (a test, "
                "a quiz, someone else's presentation) but is not spoken aloud."),
            h.p("A question is recognized by the “?” sign or by an interrogative/imperative opening of "
                "the phrase, and repeated triggers on the same text are filtered out: the text must "
                "settle (two polls in a row show the same thing), differ in the share of common words from everything recently sent (the last 16 are "
                "remembered — scrolling back to an already answered question does not send it to the LLM "
                "again) and not land in the pause after the previous answer. Every "
                "piece of screen text sent to the LLM is "
                "stored in the meeting log (role question, tag [screen]) — it will "
                "get into the meeting history and the AI minutes."),
            h.img("19-tab-ocr.png",
                  "Settings, the “Screen OCR” tab: monitor, engine, poll interval, "
                  "repeat-protection thresholds and the “Recognize now” button",
                  "wide"),

            h.h2("Monitoring settings"),
            h.table(
                ["Setting", "What it does", "Default"],
                [
                    ["Enable screen text recognition (OCR)", "Makes the feature available: "
                     "the status orb indicator and the Ctrl+Shift+X switch. The background monitoring itself is enabled by a separate checkbox below. The manual run "
                     "Ctrl+Shift+N is NOT muted by this checkbox — it always works.", "off"],
                    ["Automatically watch for screen changes", "Background monitoring: "
                     "captures on the interval, recognition and sending the question to the LLM. Works independently — with watching disabled the manual run "
                     "Ctrl+Shift+N keeps working.", "off"],
                    ["Show the status indicator on the screen", "A semi-transparent 28px orb "
                     "in the bottom right corner: green — waiting, blue — recognizing, "
                     "purple — sending to the LLM, red — error (3 s → back). Like the "
                     "overlay, it is hidden from screen capture and does not intercept clicks.", "on"],
                    ["Monitor", "Which monitor to capture: “Primary” or a specific one by number "
                     "(the label shows the system device name too).", "Primary"],
                    ["OCR engine", "“Tesseract” — built in (models in tessdata/ next to "
                     "the program); “Windows OCR (WinRT)” — the system one, usually faster, "
                     "but it requires language packs installed in Windows. Both run "
                     "on CPU — the graphics card choice physically does not apply here. If "
                     "Windows OCR is unavailable, Tesseract is used automatically.",
                     "Tesseract"],
                    ["Poll interval (sec)", "A slider from 0.5 to 10 s: how often a capture and recognition pass is made. A shorter interval — faster reaction and higher CPU load.", "2.0"],
                    ["Change threshold", "A slider from 0.1 to 0.95: the share of common words below which the "
                     "text counts as new. A lower threshold — fewer repeated sends.", "0.6"],
                    ["Pause after a found question (sec)", "A slider from 0.3 to 30 s: right after an answer new questions are not sent until the text on the screen changes.",
                     "20"],
                    ["Minimum text length (chars)", "A slider from 4 to 64: shorter — ignored, not sent to the LLM. A filter against false triggers "
                     "on interface clutter and line fragments.", "12"],
                    ["Answer automatically via LLM", "Checkbox cleared — the question is only recognized, no request goes to the model.", "on"],
                ]),

            h.h2("“Recognize now”"),
            h.p("The button under the settings and the Ctrl+Shift+N hotkey perform a single recognition pass without waiting for the next monitoring tick. The recognized "
                "text is shown in the field — that is how you check the right monitor is selected and the engine sees the text. If sending to the model is not forbidden (the LLM is on and "
                "“Answer automatically via LLM” is set), the same text goes to the LLM as a question — the answer appears in the overlay with an “OCR Xs” tag, just like in an automatic trigger."),
            h.p("The first recognition takes a few seconds: the engine is initialized in the "
                "background, so the window does not freeze. If the engine is unavailable (Tesseract models or Windows language packs missing), the field shows the reason, and "
                "monitoring simply will not trigger — the rest of the features keep "
                "working. The time of each stage of every pass (capture/preprocessing/"
                "recognition) is written to the log — it shows where time is spent."),
            h.p("If you press Ctrl+Shift+N again while the previous capture is still being recognized, the request is not lost: it enters a queue (FIFO) and is processed next. The position in the queue (“Queued · position N”) is visible in the Memory Center on the task card."),

            h.p("Two indicators report the state: the semi-transparent 28px orb in the bottom right corner of the screen (green — waiting, blue — recognizing, purple — sending to the LLM, red — error; hidden from screen capture and click-proof) and the overlay status line (“OCR: watching the screen”, “OCR: question spotted, sending…”, “done”). Under the text, such a question bubble carries an “OCR Xs” tag — how long recognition took."),
            h.p("The most convenient way to toggle the feature is the Ctrl+Shift+X hotkey (the shortcut is changed on the “Hotkeys” tab); since 2.16.0 this hotkey toggles the whole feature — background watching lives in the separate “Automatically watch for screen changes” checkbox, and Ctrl+Shift+N works even when watching is off."),
        ])),

        ("The “Hotkeys” tab", "".join([
            h.p("Global shortcuts are the main way to control BrainstormBuddy without opening "
                "windows: the hotkeys work from any program while the assistant is running. The "
                "“Hotkeys” tab (an item in the left settings menu) contains eight editable fields — each with its own action label."),
            h.img("09-tab-hotkeys.png",
                  "The settings window, “Hotkeys” tab: eight fields with shortcuts",
                  "wide"),

            h.h2("Assignable shortcuts"),
            h.table(
                ["Action", "Default shortcut", "What happens"],
                [
                    ["Show/hide overlay", "Ctrl+Shift+H",
                     "Hides the hint window or brings it back to the screen."],
                    ["Change opacity", "Ctrl+Shift+O",
                     "Cyclically adds 20% of opacity: 0.9 → 1.0 → 0.4 → … "
                     "The new value is shown with a toast."],
                    ["Open settings", "Ctrl+Shift+S",
                     "Opens (or brings to the front) the settings window."],
                    ["Pause audio", "Ctrl+Shift+P",
                     "Stops and resumes audio capture. Before pausing, the unfinished phrase is flushed to recognition so it does not “freeze” in the buffer."],
                    ["Karaoke overlay (on/off)", "Ctrl+Shift+K",
                     "Shows or hides the karaoke teleprompter strip with one press "
                     "(a toggle). The state is saved "
                     "in the config — after a restart the mode stays the same."],
                    ["Screen OCR (on/off)", "Ctrl+Shift+X",
                     "Enables or disables the screen OCR feature entirely (the orb indicator, "
                     "the “Recognize now” button, background watching if it is enabled "
                     "by its checkbox). The state is saved in the config."],
                    ["Screen OCR — recognize now", "Ctrl+Shift+N",
                     "Forces a single recognition pass without waiting for the next monitoring tick. With the LLM enabled the recognized text goes to the model as a question and the answer appears in the overlay; with it disabled, only the text is shown. Handy for nudging OCR manually when the question is already on the screen."],
                    ["Show tray icon", "Ctrl+Shift+T",
                     "Returns the icon to the notification area if it was hidden by the "
                     "“Hide tray icon” setting (the “Overlay” tab). Triggering it clears "
                     "the hiding in the config — the icon stays visible after a restart too."],
                ]),
            h.p("The format is simple: modifiers and the key are listed with plus signs, for example Ctrl+Shift+H or Win+Alt+Q. The modifiers Ctrl, Shift, Alt, Win and the keys A–Z, 0–9, F1–F12, Space, Tab, Esc are supported — a hint about this is written right on the tab. Letter case and modifier order do not matter."),

            h.h2("Built-in shortcuts that are not on the tab"),
            h.p("Three more hotkeys are hard-wired into the program and cannot be changed in the UI:"),
            h.ul([
                "Ctrl+Shift+L — open/close the log window;",
                "Ctrl+Shift+Y — pause sending to the LLM: questions do not go to the model, while recording and recognition continue;",
                "Ctrl+Shift+C — “screenshot mode”: the overlay temporarily appears in screen capture so you can take a picture of the hints yourself; pressing it again hides the window from capture again.",
            ]),

            h.h2("How to change a shortcut"),
            h.ol([
                "Open the settings (Ctrl+Shift+S) → “Hotkeys”.",
                "Type the new combination into the right field — for example, replace "
                "Ctrl+Shift+H with Ctrl+Alt+H.",
                "Press “Save” at the bottom of the window.",
                "Restart BrainstormBuddy: shortcuts are registered in the system once when the overlay is created, so new values are only picked up on the next start.",
            ]),
            h.callout("warn", "“Some hotkeys could not be registered”",
                      "Such a toast when the overlay appears means that at least one shortcut is taken — another program has already registered it (Windows gives a combination to only one application). Come up with a different combination on the “Hotkeys” tab, save and restart. Which hotkeys failed is visible in app.log in the line “Hotkeys: toggle=…, opacity=…, …” — false next to an action means a conflict."),
            h.callout("info", "Holding a key does not spam",
                      "Hotkeys are registered with the “no repeat” flag: if you press and hold a combination, the action fires once, not ten times."),

            h.h2("Where the logs are"),
            h.p("Every press is noted in the log (“Hotkey pressed: toggle” and so on), and the registration result — when the overlay is created. File: "
                "%APPDATA%\\BrainstormBuddy\\logs\\app.log, quick view — Ctrl+Shift+L."),
        ])),

        ("The tray icon", "".join([
            h.p("While BrainstormBuddy is running, its icon with the “BrainstormBuddy” tooltip sits in the notification area (next to the clock). On Windows 10/11 it may hide in the “^” overflow — if you like, drag it with the mouse to a visible spot. Double-clicking the icon brings the overlay back to the screen, right-clicking opens the menu."),
            h.img("16-tray-menu.png",
                  "The context menu of the BrainstormBuddy tray icon", "medium"),

            h.h2("Menu items"),
            h.table(
                ["Item", "What it does"],
                [
                    ["Show overlay", "Brings the hint window back to the screen (the same as Ctrl+Shift+H on a hidden window)."],
                    ["Hide overlay", "Removes the window from the screen — the program keeps listening and answering, and you can show it at any moment."],
                    ["Settings", "Opens the settings window (Ctrl+Shift+S)."],
                    ["Show logs", "Opens the live log window (Ctrl+Shift+L)."],
                    ["Pause audio capture", "Stops/resumes the recording of the microphone and system sound (Ctrl+Shift+P)."],
                    ["Pause sending to the LLM", "Blocks sending questions to the model — recognition and recording continue (Ctrl+Shift+Y)."],
                    ["Clear history", "Wipes the list of questions and answers accumulated in the overlay."],
                    ["Exit", "Ends the program: the config is saved, hotkeys are unregistered, and the icon disappears from the tray."],
                ]),
            h.p("The pause items work as toggles: press once — the mode turns on, press again — it turns off. Every toggle is confirmed by a popup notification (“Paused”, “Audio capture stopped”, “LLM resumed”), and the chosen state is remembered and survives a restart — handy to switch the microphone off before a meeting and not worry about it turning itself back on afterwards."),

            h.h2("When the window “disappeared”"),
            h.p("If you minimize the overlay with its window button, the program shows a hint once: “Overlay minimized — bring it back: tray icon → „Show overlay“, or Ctrl+Shift+H”. So a missing window is not an exit from the program: it lives in the tray, and the hints are one click away."),
            h.p("The “Hide tray icon” checkbox (the “Overlay” tab) removes the icon from the notification area entirely — the program keeps working unnoticed. Bring the icon back: press Ctrl+Shift+T (the shortcut is reassignable on the “Hotkeys” tab) — it also clears the hiding in the config. If you launch the program a second time while the icon is hidden, the hint will remind you about this hotkey."),

            h.callout("info", "The tray is the only “off switch”",
                      "While the tray icon is visible, BrainstormBuddy is working: it listens to audio, recognizes speech and answers, even if the overlay is hidden. Fully exit the program only via “Exit” in this same menu."),
        ])),
    ]
