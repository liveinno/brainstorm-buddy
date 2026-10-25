# -*- coding: utf-8 -*-
def chapters(h):
    return [
        ("Transcribing audio and video files", "".join([
            h.p("Besides live meetings, BrainstormBuddy can transcribe ready-made recordings — "
                "audio and video. Everything happens locally, on your computer: the recording "
                "is not sent anywhere and no internet is needed (except for downloading ffmpeg "
                "and for the AI summary — both covered below)."),
            h.p("The result is a transcript with time codes [mm:ss], optionally an AI summary in "
                "the “Meeting minutes” format, and you can copy it all or save it as .txt."),

            h.h2("How to open the transcription window"),
            h.ul([
                "From the settings window: open the “File Transcription” tab and click "
                "“Open file transcription →”.",
                "From the command line: run BrainstormBuddy.exe --file-transcription — the window "
                "opens right after startup.",
            ]),
            h.p("The window is called “File Transcription”. If it is already open, calling it "
                "again simply brings it to the front."),

            h.h2("Step by step: from files to text"),
            h.img("14-file-transcription.png",
                  "The “File Transcription” window: history on the left, the queue of file cards on the right", "wide"),
            h.ol([
                "Click “Select files” and pick one or several recordings (the dialog allows "
                "multi-selection). Supported are mp4, webm, mkv, mov, avi, m4a, mp3, wav, aac, "
                "ogg, opus, flac — plus any other file via the “All files” item (as long as it "
                "has an audio track).",
                "Optionally pick the engine and hardware in the top right corner (details below).",
                "Click “Transcribe”. Files are processed one by one — there is a single model, so "
                "strictly sequential: the header shows “File i/N: name”, each file gets its own "
                "card with a status (queued / processing / done / error) and progress.",
                "The finished text appears right in the file card in the “TRANSCRIPT” block. If "
                "there is no recognizable speech in the recording, you will see “(no speech "
                "recognized)”.",
            ]),
            h.p("An error in one file does not stop the queue — the problem card is marked "
                "“error” and the next file starts automatically. The “Cancel” button during "
                "processing stops the queue: the current and untouched files return to "
                "“queued” status (the interrupted one gets the note “Stopped — the file stayed "
                "in the queue”); “Transcribe” later finishes the rest."),
            h.img("14b-file-queue-cancelled.png",
                  "The queue after canceling: all files are “queued” again, the interrupted one is marked “Stopped”", "wide"),

            h.h2("What to do with the result"),
            h.p("Each block has its own buttons above it:"),
            h.ul([
                "“Copy transcript” — the whole text to the clipboard.",
                "“Download .txt” — save to a file. The name is suggested automatically: "
                "<recording name>_transcript.txt for the transcript and <recording name>_summary.txt for the summary.",
                "“🧠 Extract to memory” — the LLM extracts facts from the transcript and "
                "stores them in Buddy Memory. A dialog opens with a field for your own "
                "extraction rules (“only agreements and deadlines” and so on, can be "
                "remembered) and a list of found facts with checkboxes — the checked ones are "
                "saved. With memory disabled the dialog shows a placeholder and does nothing.",
            ]),
            h.p("The splitter between the blocks can be dragged with the mouse: it changes the "
                "proportions of the transcript and the summary."),

            h.h2("AI summary: meeting minutes"),
            h.p("After transcription the “Generate summary” button becomes active. "
                "BrainstormBuddy sends the transcript with time codes to the LLM configured on "
                "the “LLM” tab (local Ollama or a cloud provider) and receives a structured "
                "protocol: topic, participants and roles, the course of the meeting by time "
                "codes, agreements and open questions."),
            h.p("For long recordings the summary is assembled in parts: the program splits the "
                "text, summarizes each part and merges everything into a single protocol — the "
                "status line shows “Minutes · analyzing part N/M…”, then “Minutes · merging…”. "
                "This can take several minutes."),
            h.callout("warn", "The summary needs an LLM",
                      "Recognition itself works offline, but the summary does not: it needs a "
                      "running Ollama or a reachable cloud provider (Settings → LLM). If the "
                      "button returns “LLM is unavailable or did not respond” — check those "
                      "settings. The quality of the minutes depends on the model: a small local "
                      "3B generalizes more roughly than a 7B or the cloud."),

            h.h2("Engine and hardware"),
            h.p("In the top right corner of the window there are two dropdowns:"),
            h.table(
                ["Setting", "Options", "What to choose"],
                [
                    ["Recognition engine",
                     "“GigaAM · fast (Russian)” or “Whisper turbo · quality, punctuation”",
                     "GigaAM — fast and built in, great for Russian speech; the label shows the "
                     "actual model version (v3 or v2). Whisper turbo — more careful with "
                     "punctuation and mixed speech, but the model (~570 MB) must be downloaded "
                     "in advance: Settings → Speech recognition (STT). The first Whisper start "
                     "takes ~10 seconds to load the model."],
                    ["Hardware",
                     "“Auto (same as settings)”, “CPU” or “GPU: #N <name>”",
                     "Auto is a sensible default. An explicit GPU is usually faster on long "
                     "recordings. If the selected GPU disappears from the system (for example, "
                     "an external card was unplugged), the choice falls back to “Auto” on the "
                     "next opening by itself."],
                ]),
            h.p("Under the lists there is a status hint, for example “GigaAM v3 — fast, Russian · auto · ffmpeg ✓” "
                "or “⚠ Whisper model not downloaded”. The hardware choice is remembered and applies "
                "only to file transcription — live meeting recognition is not affected."),

            h.h2("History"),
            h.p("On the left there is the “HISTORY” list: every completed transcription is saved "
                "with the file name and date. Clicking an entry brings back its transcript and "
                "summary — no need to recognize again. If the source file was moved or deleted, "
                "a “(file moved)” note appears next to the name: the text stays available, but "
                "the “Transcribe” button becomes inactive."),
            h.p("To delete an entry — right-click it → “Delete from history”. The history itself is stored "
                "in %APPDATA%\\BrainstormBuddy\\transcriptions."),

            h.h2("webm, mkv, opus and ffmpeg"),
            h.p("The formats mp4, mp3, m4a, wav, mov, avi, aac are recognized right away. webm, mkv, ogg, "
                "opus, flac and oga need ffmpeg — a helper program for extracting sound. If it is "
                "missing, picking such a file shows a yellow banner “⚠ webm / mkv / "
                "opus files require ffmpeg” with a “Download ffmpeg (~80 MB)” button — click it and the "
                "program downloads and installs ffmpeg by itself (internet required, up to 15 minutes "
                "on a slow connection)."),
            h.p("Three ways to get ffmpeg, in search priority order:"),
            h.ol([
                "The “ffmpeg — webm/mkv/opus support” component in the installer (offered when "
                "the file was embedded in the build).",
                "The “Download ffmpeg (~80 MB)” button in the transcription window — it puts ffmpeg.exe in "
                "%APPDATA%\\BrainstormBuddy\\ffmpeg.",
                "Manually: put ffmpeg.exe in %APPDATA%\\BrainstormBuddy\\ffmpeg or in any "
                "folder from the system PATH.",
            ]),
            h.callout("info", "Gotchas",
                      "• A recording without distinguishable speech returns “(no speech recognized)” — that is not a failure. "
                      "• You cannot drag files into the window, only the “Select files” button works. "
                      "• Files are processed sequentially (a single model); each file gets its "
                      "own card with its own copy/txt/summary buttons and "
                      "its own history entry. • Errors are shown right in the window: the status line "
                      "turns red and starts with “⚠” — the error text helps you understand the cause "
                      "(file moved, ffmpeg missing, engine unavailable)."),

            h.h2("Where the logs are"),
            h.p("Window events go to the common log %APPDATA%\\BrainstormBuddy\\logs\\app.log "
                "(UI and Ai categories): hardware changes, recognition and summary errors. "
                "The easiest way to open the log is the “Show logs” item in the tray menu "
                "or the shortcut "),
            h.kbd("Ctrl+Shift+L"),
            h.p("— the log window opens on top of the others."),
        ])),

        ("The “File Transcription” tab in settings", "".join([
            h.p("This is the simplest settings tab: it configures nothing and serves as the "
                "“entry hall” to the separate transcription window."),
            h.img("06-tab-file.png",
                  "Settings, the “File Transcription” tab: description and the button that opens the window", "wide"),
            h.p("The tab has the “Audio/video transcription” title, a short description of the "
                "capabilities and the single “Open file transcription →” button, which opens the window "
                "from the previous chapter."),
            h.p("All the real settings — engine choice (GigaAM/Whisper), hardware (CPU/GPU), "
                "language and the Whisper model — live not here but in the transcription window itself and on the "
                "“Speech recognition (STT)” tab. If you are looking for where to download a Whisper model or pick a GPU — "
                "open the corresponding tabs, not this one."),
        ])),
    ]
