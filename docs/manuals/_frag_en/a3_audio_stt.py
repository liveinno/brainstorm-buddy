# -*- coding: utf-8 -*-
def chapters(h):
    return [
        ("The “Audio” tab: microphone and meeting sound", "".join([
            h.p("The “Audio” tab configures everything the app hears: your microphone, system "
                "sound (the voices of the people you talk to) and the rules that decide when an "
                "utterance counts as finished and is sent for recognition."),
            h.img("05-tab-audio.png", "The “Audio” tab: speech segmentation mode, device selection, microphone test and thresholds", "wide"),

            h.h2("Two sound sources: you and your conversation partners"),
            h.p("The app listens to two channels at once and mixes them into a single stream:"),
            h.ul([
                "“Microphone (input)” is your voice. The “Microphone (input)” list shows every active Windows recording device; the “Default” item takes the system microphone.",
                "“Speaker (loopback)” is the sound Windows plays to speakers or headphones: your colleagues' voices from Zoom, Teams, the browser. Technically this is recording from a playback device, so conversation partners are captured without any second microphone. The “Default” item follows the system output device.",
            ]),
            h.p("Mixing matters so the assistant understands both your colleagues' questions and "
                "your own answers in one context. Two flags control the channels:"),
            h.ul([
                "“Microphone only (no loopback)” — the app stops listening to system sound. Your partners' questions will not reach recognition; only your voice remains. Enable it when you are speaking aloud yourself or the discussion happens in the room.",
                "“Listen to microphone in real time (recognize your speech)” — on by default. If cleared, your microphone is not recognized: the app hears only your partners through loopback.",
            ]),
            h.callout("warn", "If loopback is unavailable",
                      "The app does not crash: a “Loopback init failed, continuing with mic only” warning goes to the log and work continues with the microphone only. If the selected output device disappears, the program falls back to the system default device. And when the default device changes (you plugged in headphones), loopback restarts automatically."),

            h.h2("Microphone test"),
            h.p("Click “Test microphone” and say something — the six bars next to the button "
                "should flicker in time with your voice. The button turns into “Stop”; pressing "
                "it again ends the test."),
            h.callout("info", "The bars are not moving",
                      "Check that the right microphone is selected in the list, and its level in Windows sound settings. The test itself recognizes nothing and sends nothing anywhere — it only shows loudness."),

            h.h2("Speech segmentation mode"),
            h.p("The app has to understand where an utterance ends so it can send it for "
                "recognition. The “Speech segmentation mode” list controls this:"),
            h.table(
                ["Mode", "How it works"],
                [
                    ["Manual (presets below)", "You set the pause and thresholds yourself — via a preset or the sliders."],
                    ["Auto — by pauses (default)", "The app measures speech rhythm and adapts the pause threshold to the speaker's pace: fast speech gets a shorter pause, slow speech a longer one."],
                    ["Auto — smart glue", "On top of that it glues cut-off thoughts: an utterance ending on a conjunction or preposition is not split, the app waits for the continuation. The best option for live dialogue."],
                ]),
            h.p("In the auto modes the “Silence threshold” slider is hidden — automation drives "
                "it. The line “Current pause threshold (auto): … speaker · … mic” shows the "
                "selected values separately for each channel. The rest of the tab's settings "
                "work in every mode."),

            h.h2("Presets and thresholds (manual mode)"),
            h.p("In manual mode the “Audio preset” list becomes available — one selection sets "
                "four sliders:"),
            h.table(
                ["Preset", "Silence threshold", "Min. speech", "Pre-roll", "Post-roll"],
                [
                    ["Standard", "1800 ms", "1000 ms", "400 ms", "500 ms"],
                    ["Fast speech", "600 ms", "300 ms", "150 ms", "150 ms"],
                    ["Slow/Thoughtful", "2000 ms", "2000 ms", "500 ms", "500 ms"],
                    ["Custom", "your slider values", "—", "—", "—"],
                ]),
            h.table(
                ["Slider", "Range", "What it does"],
                [
                    ["Silence threshold (voice → silence)", "500–2500 ms, default 1800", "How much silence counts as the end of an utterance. Lower — faster reaction, but a phrase may get cut off; higher — safer for slow speech, but the answer arrives later."],
                    ["Minimum speech duration", "200–1500 ms, default 400", "Filters out short junk — clicks, coughs, rustles. If the app misses short utterances, decrease it."],
                    ["Pre-roll", "100–500 ms", "How much sound BEFORE speech starts is kept in the chunk — the first sounds of a phrase are not cut off."],
                    ["Post-roll", "100–500 ms", "How much sound AFTER speech ends is captured — the last syllables are not cut off."],
                ]),

            h.h2("Sensitivity and noise"),
            h.p("The “RMS threshold (mic sensitivity)” is the boundary below which sound counts "
                "as silence. The range is 0.001–0.1, default 0.004: the noise floor of weak "
                "microphones is cut off while quiet speech is caught reliably."),
            h.p("Under the slider there is a live “Live RMS: … · threshold: …” indicator — the "
                "value refreshes about 7 times a second from the audio stream, and the dot "
                "lights up when loudness is above the threshold. This is the fastest way to "
                "make sure the app “hears” you and to tune the threshold to your voice."),
            h.ul([
                "“Threshold auto-calibration” — off by default, the manual threshold applies. Enable it if the background really is noisy and false triggers started: the threshold will automatically stay just above the noise level.",
                "“Speech detection sensitivity” — the threshold headroom above background noise when auto-calibration is on: from “0 — Conservative (minimum false triggers)” (default) to “3 — Sensitive + noise filter (ZCR)”. With a manual threshold only mode 3 has an effect — it adds a noise filter.",
                "“Audio debug logs” — writes RMS to the log every second. Needed only for diagnostics; leave it off in normal use.",
            ]),
            h.callout("warn", "Typical symptoms",
                      "Ends of phrases get cut — raise the “Silence threshold” or switch to “Auto — smart glue”. Noise and clicks get caught — raise the RMS threshold or enable auto-calibration. Short utterances get missed — decrease the “Minimum speech duration”. The log window and the %APPDATA%\\BrainstormBuddy\\logs\\app.log file help you check the behavior."),
        ])),

        ("The “Speech recognition (STT)” tab: engines", "".join([
            h.p("The “Speech recognition (STT)” tab turns sound into text. Here you pick the "
                "recognition engine, download models and check the speed."),
            h.img("07-tab-stt.png", "The “Speech recognition (STT)” tab: hardware check, engine choice and acceleration", "wide"),

            h.h2("Where to start: hardware check"),
            h.p("The “Check hardware” button instantly rates the CPU, memory and graphics card "
                "and suggests which engine your hardware can handle — the result and "
                "recommendation appear right under the button."),

            h.h2("Choosing an engine"),
            h.p("The “STT engine” list offers five options. The default is the built-in GigaAM "
                "v3: it works locally, with no internet and no Docker."),
            h.table(
                ["Engine", "What it is", "When to pick it"],
                [
                    ["Built-in GigaAM v3 (default)", "The GigaAM v3 E2E CTC INT8 model (v3_e2e_ctc.int8.onnx, ~215 MB) runs inside the app itself. Noticeably lighter and faster than v2 at the same Russian-speech quality.", "The primary option for a live assistant."],
                    ["Built-in GigaAM v2", "The previous v2_ctc.onnx model (~930 MB), char-CTC. Kept for compatibility; if the v3 files are missing, the app falls back to it automatically.", "A fallback / older installations."],
                    ["Whisper large-v3-turbo", "The ggml-large-v3-turbo-q5_0 model (~547 MB) via whisper.cpp. Adds punctuation, capitalization, English and honest timestamps.", "When quality matters more than speed. Too heavy for live work — better on a GPU."],
                    ["Local server in Docker (GigaAM)", "The app brings up the brainstorm-local-stt container on localhost:8765 itself — no address to enter. Requires Docker Desktop; the first run downloads the model (~450 MB), which can take up to 5 minutes.", "If you need an isolated local server and Docker is already installed."],
                    ["External server (custom address)", "Any [OI]-compatible server with the …/audio/transcriptions method (for example, a faster-whisper server on your network). Enter the “Server address” and the “Model name on the server” (for example, gigaam-v2 or whisper-1). An empty address = the LLM address is used.", "When recognition already runs on your own server."],
                ]),
            h.p("An engine switch applies right after “Save”: the new engine loads in the "
                "background in a couple of seconds, no restart needed. For the Docker option "
                "the tab has container control buttons — “Start”, “Stop”, “Remove”, “Ping” — "
                "a status indicator, metrics (CPU, RAM, ping, uptime) and the build/start log."),

            h.h2("GPU acceleration"),
            h.p("Each built-in engine has its own “Acceleration” list and its own “Graphics "
                "card” list:"),
            h.ul([
                "GigaAM: “CPU — works everywhere (recommended)” (default), “GPU · DirectML (pick the graphics card below)”, “Auto (discrete GPU if present, else CPU)”.",
                "Whisper: “Auto — GPU (Vulkan), else CPU” (default), “CPU only”, “GPU · Vulkan (pick the graphics card below)”. Vulkan works on NVIDIA, AMD and Intel through the regular driver — no CUDA needed; if the device is not found, the engine falls back to CPU.",
            ]),
            h.callout("warn", "Integrated graphics can be slower",
                      "On weak integrated graphics such as Intel UHD the GPU mode is often slower than CPU — keep CPU. A discrete card (NVIDIA/AMD) speeds things up noticeably, Whisper especially."),

            h.h2("Downloading and storing models"),
            h.p("Models are downloaded once with the “Download model” button — progress and "
                "status are visible under it. Files live in %APPDATA%\\BrainstormBuddy\\models: "
                "GigaAM v3 ~215 MB (v2 ~930 MB), Whisper ~547 MB. The “Delete model” button "
                "frees the space. If no local model is present, the app temporarily works "
                "through an external server and shows a notification."),
            h.callout("info", "The first recognition takes longer — that is normal",
                      "The model is loaded into memory at startup. The app warms the engine up in the background at start and after settings changes, so the first real utterance usually does not pay the “cold start”."),

            h.h2("Measure speed"),
            h.p("The “Measure speed” button runs a three-second reference fragment through the "
                "active engine and shows the time in milliseconds and the RTF — the real-time "
                "factor (RTF 0.3 means a minute of speech is recognized in ~18 seconds; lower "
                "is faster). This is an honest way to compare CPU and GPU, or GigaAM and "
                "Whisper, on your hardware."),

            h.h2("Speakers (diarization)"),
            h.p("The “Speakers” group at the bottom of the tab enables identifying who is "
                "speaking in the partner channel (speaker). Off by default — nothing changes. "
                "When on, every partner utterance gets a label S1, S2 and so on (one person — "
                "one number within a call): a chip with the number appears in the question "
                "bubble, in karaoke the line starts with “S1: …”, and an exported transcript "
                "shows “S1: utterance”. The labels are visible in the LLM context too — "
                "answers can tell participants apart."),
            h.ul([
                "“Split speakers by voice (diarization)” — on/off. Applies immediately, no restart needed.",
                "“Backend” (visible when diarization is on) — Auto (GPU→CPU), CPU, Vulkan or CUDA; the mode depends on the installed bundle. cpu↔vulkan switches apply immediately; changing the bundle type (Vulkan→CUDA) requires a restart.",
            ]),
            h.p("Diarization runs fully locally and goes in parallel with recognition — it adds "
                "almost no latency. The engine is Nemotron-3 (Sortformer): the GGUF model "
                "Nemotron-3-Diarization.Q8_0.gguf in {app}\\models plus the nemo-speech bundle "
                "(~56 MB) in {app}\\nemo-speech; it works in real time on a Vulkan GPU or the "
                "CPU and detects the number of speakers automatically — there are no thresholds "
                "to tune. Only the voice from the speaker channel is identified; your "
                "microphone voice is always signed “You”. If the model is not found, the status "
                "in the group honestly says “model missing” and the feature switches itself "
                "off — recognition keeps working as before."),
            h.callout("info", "Labels live within a call",
                      "The S1/S2/… numbering resets when a new chat starts (the reset button in the overlay) — that is a new set of participants. If a voice on one utterance could not be identified confidently, the last known speaker of that call is used."),

            h.h2("Labels inside an utterance (2.14)"),
            h.p("If conversation partners interrupt each other inside a single utterance, the speaker label "
                "now switches right inside the text: “[S1] … [S2] …” — instead of the "
                "whole chunk going to one dominant voice. The markup is assembled from "
                "GigaAM (v2/v3) word-level timestamps and diarization spans; if timestamps "
                "are unavailable (another engine), a fallback applies — an audio cut at "
                "span boundaries or a proportional estimate by duration. Micro-insertions "
                "shorter than one diarization segment (hundreds of milliseconds) are not labeled."),
            h.p("The question bubble shows a chip for every speaker in the utterance: “[S1] … [S2] …” "
                "gives two chips, not only the dominant one. Right-clicking any of them "
                "opens “Assign name…” — the alias applies to all bubbles of the meeting."),

            h.h2("If the engine is unavailable"),
            h.p("The app is not left without recognition and walks through fallback options:"),
            h.ul([
                "Whisper is selected but the model is not downloaded → the built-in GigaAM v3 works (no files → v2, then an external server).",
                "Built-in GigaAM v3 is selected but the model files are missing → GigaAM v2 is tried, then Whisper, then the external server.",
                "An external server or Docker is selected but does not respond → the app falls back to the local engines (GigaAM v3 → v2 → Whisper) if their models are downloaded.",
            ]),
            h.p("Each such fallback comes with a notification and a log entry tagged “Ai” and "
                "“LocalStt” — see the log window or %APPDATA%\\BrainstormBuddy\\logs\\app.log."),
            h.callout("danger", "Recognition is silent",
                      "Download a local model (GigaAM is the simplest path) or check the external server address with the “Ping”/“Measure speed” button: if the server does not respond, the message there will say so directly."),
        ])),
    ]
