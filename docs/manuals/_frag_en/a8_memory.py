# -*- coding: utf-8 -*-
"""User guide fragment: Buddy Memory - the "Memory" tab in settings, the
Memory Center, "Ask Memory", the pre-meeting brief and the post-meeting
review window."""


def chapters(h):
    return [
        ("Memory (Buddy Memory)", "".join([
            h.p("Buddy Memory is the long-term memory of your meetings: the app "
                "remembers decisions, tasks, deadlines and facts from "
                "conversations and injects them into the context of future "
                "calls. After a few meetings the assistant already “remembers” "
                "what was agreed last week and does not ask you about it "
                "again."),
            h.p("Since version 2.16.0 memory is enabled by default: the "
                "“Meeting memory” flag and both sub-flags (read and write) are "
                "set right after installation. It can be disabled at any time "
                "on the “Memory” tab in settings - already stored data is not "
                "deleted by that. All data lives locally in a single SQLite "
                "file - %APPDATA%\\BrainstormBuddy\\memory.db - and is "
                "searched through the FTS5 full-text index (if it is suddenly "
                "missing from the SQLite build, an ordinary LIKE search kicks "
                "in). No cloud is used: only the fact-extraction prompt text "
                "leaves the machine, and only when an LLM is enabled for "
                "memory."),

            h.h2("How to enable or disable"),
            h.p("Settings → the “Memory” tab → the “Meeting memory (Buddy "
                "Memory)” flag. Two independent flags sit next to it: “Read "
                "memory in call” (found facts are mixed into the context of "
                "LLM answers) and “Remember meeting” (after the meeting, "
                "facts are extracted from the transcript). They can be "
                "enabled separately - for example, read memory but write "
                "nothing new."),
            h.p("After enabling, statistics appear at the bottom of the tab "
                "(“N facts · M meetings · …”), and the tray icon menu gains a "
                "“Memory” item that opens the Memory Center. The service "
                "starts right on “Save” - no app restart needed."),
            h.img("32-tab-memory.png",
                  "The “Memory” tab in settings: the “Meeting memory (Buddy "
                  "Memory)” flag and the save modes",
                  "wide"),

            h.h2("Save mode"),
            h.p("The “Save mode” field decides how thoroughly a meeting turns "
                "into memory:"),
            h.table(
                ["Value", "What is saved after the meeting"],
                [
                    ["Disabled", "Nothing. The meeting is written to the "
                     "journal as usual, but does not reach memory."],
                    ["Summary only", "Only the final meeting summary - no "
                     "facts are extracted separately."],
                    ["Smart memory (facts + summary)", "The LLM parses the "
                     "transcript and puts separate facts into memory: "
                     "decisions, tasks, deadlines, open questions - plus the "
                     "summary. The main working mode."],
                    ["Full archive", "Everything is saved, including the "
                     "detailed transcript that anchors facts to lines. More "
                     "data for “Show source”, but memory.db grows faster."],
                ]),

            h.h2("Extraction review"),
            h.p("The “Extraction review” field decides whether fresh facts "
                "enter memory right away or through your eyes:"),
            h.table(
                ["Value", "How it works"],
                [
                    ["Automatic", "Extracted facts immediately become active "
                     "and take part in recall at the following meetings."],
                    ["Review before saving", "After the meeting, the “Memory "
                     "check after meeting” window opens with the list of "
                     "extracted items and checkmarks. “Save selected”, “Save "
                     "all”, or “Discard all”. If you simply leave the window "
                     "alone, it closes itself after 30 seconds and saves the "
                     "selected items; Esc or the cross closes without saving "
                     "(the facts stay in “Pending” status)."],
                    ["Manual only", "Facts are extracted but stay in "
                     "“Pending” status - you confirm them yourself in the "
                     "Memory Center when convenient."],
                ]),
            h.callout("info", "A tip for the beginning",
                      "Set “Review before saving” for the first weeks: you "
                      "will see what exactly the app counts as a fact, and "
                      "the unnecessary will not reach the context of future "
                      "answers."),

            h.h2("Memory model and embeddings"),
            h.ul([
                "“Memory model”: “Same as assistant” (the main provider), "
                "“Separate model” (its own provider and model for "
                "extraction) or “Disabled” - fact extraction does not work, "
                "memory is available read-only and for manual input.",
                "“Embeddings”: “Disabled” (full-text search only), “Local "
                "(multilingual)” (the E5 model computes on your machine, "
                "semantic search without the network) or “OpenAI-compatible "
                "API” (vectors are computed by an external service). "
                "Changing the mode starts a background reindex - it is "
                "visible on the “Jobs” tab of the Memory Center.",
                "“Automatic recall” injects found facts into LLM answers; "
                "next to it are the “Max memory tokens in context” and “Max "
                "facts in context” limiters. Search is time-bounded and "
                "never delays the answer.",
            ]),

            h.h2("Search over past meetings"),
            h.p("The “Search past meetings for answers” flag enables mixing "
                "fragments of past meeting journals into the answer context. "
                "An unticked flag means answers do not use meeting history. "
                "The “Meeting search timeout” slider sets how long the "
                "search may take (500-10,000 ms, 1500 ms by default) before "
                "answering without it. If the timeout fires, exact word "
                "matches are preserved - only semantic results may be "
                "missing; the answer is marked as incomplete."),

            h.p("The local mode is the intfloat/multilingual-e5-small model "
                "(MIT license, ONNX, CPU only): the weights are ~470 MB "
                "(FP32). In the Full install the model already sits in the "
                "program's models\\e5 directory - semantic recall works "
                "right away and without the network; in the compact (Lite) "
                "build the weights are downloaded once with a button on the "
                "tab into %APPDATA%\\BrainstormBuddy\\models\\e5. The model "
                "loads lazily - it enters memory at the first semantic "
                "request, not at app start, and unloads after idle. On CPUs "
                "with AVX-512 VNNI a quantized INT8 build (~113 MB) is "
                "fetched automatically when downloading. Since 2.16.0 memory "
                "is enabled by default - it can be disabled with the "
                "checkbox on the same tab if desired."),

            h.h2("Memory on the assistant's request"),
            h.p("If during a meeting you ask the assistant to “remember "
                "that…” (or “forget …”, “fix in memory …”), the model adds a "
                "hidden directive [[MEMORY_ADD]] / [[MEMORY_UPDATE]] / "
                "[[MEMORY_FORGET]] at the start of the answer. The app parses "
                "it, writes to memory immediately and shows a green toast (5 "
                "seconds) with what exactly was saved. The directives "
                "themselves are not visible to the user - neither in the "
                "answer bubble, nor in the karaoke feed, nor in the journal. "
                "Writing fires only when “Remember this meeting” is enabled "
                "for the meeting (the green memory chip)."),

            h.h2("Extracting facts from a transcription file"),
            h.p("In the “File transcription” window, once the result is "
                "ready, there is an “🧠 Extract to memory” button: a dialog "
                "opens where you can set your own extraction rules "
                "(“agreements and deadlines only” and so on, the field can "
                "be remembered), then the LLM parses the transcript and "
                "shows the list of found facts with checkmarks - only the "
                "ticked ones are saved. With memory disabled, the dialog "
                "honestly reports it and does nothing."),


            h.h2("Memory Center"),
            h.p("A separate window, “Memory - Memory Center”, opens from the "
                "tray icon menu → “Memory” or with the “Memory Center” "
                "button on the “Memory” tab in settings. It is the control "
                "panel for everything accumulated: five tabs, search and "
                "filters on top, statistics at the bottom."),
            h.img("33-memory-center.png",
                  "Memory Center: the “Facts”, “Meetings”, “Projects”, "
                  "“People” and “Jobs” tabs",
                  "wide"),
            h.table(
                ["Tab", "What is inside"],
                [
                    ["Facts", "All memory records with a scope (global / "
                     "project / person / meeting), a type (decision, task, "
                     "deadline, open question, etc.) and a status (active, "
                     "pending, superseded, invalidated). Search and filters "
                     "by scope, type and status sit above the list."],
                    ["Meetings", "A dated list of meetings: summary, "
                     "duration, the number of extracted facts and a button "
                     "to open the journal file."],
                    ["Projects", "Projects with description and notes: "
                     "“New”, “Archive”/“Unarchive”, “Set active”. The active "
                     "project is the default scope for new facts and "
                     "recall."],
                    ["People", "Interlocutors and the facts linked to "
                     "them."],
                    ["Jobs", "Background memory jobs (extraction, embedding "
                     "reindex, cleanup): status, attempt count, “Retry” for "
                     "the failed, “Clear failed”. A long fact extraction "
                     "shows a live percentage over processed chunks and a "
                     "“Stop” button; an interrupted job resumes where it "
                     "stopped without reprocessing the finished chunks."],
                ]),
            h.p("The fact list supports multi-select (Ctrl/Shift or the "
                "“Select all” button): “Delete selected” erases all ticked "
                "facts with one confirmation and a counter, the Delete key "
                "works the same way - cleaning large lists does not require "
                "a click on every record."),
            h.p("A right click on a fact opens a context menu: “Show "
                "source” (the meeting and transcript segment the fact came "
                "from), “Edit…”, “Mark as outdated”/“Mark as actual” "
                "(version history is kept), “Delete…” (with confirmation, "
                "permanent), “Change scope”, “Change project”, “Add "
                "tag…”. The humble “pin” button raises a fact higher in "
                "recall, “History” shows the chain of versions."),
            h.p("The “Export…” and “Import…” buttons in the window unload "
                "memory to JSON or Markdown and load it back from JSON: "
                "projects and facts are transferred whole, duplicates are "
                "skipped on import with an “imported / skipped” counter. "
                "This is how memory moves to another machine or gets "
                "archived. The status bar at the bottom always shows a "
                "summary: how many facts, how many meetings and how many "
                "jobs are queued."),

            h.h2("“Ask Memory” - Ctrl+Shift+M"),
            h.p("The global hotkey " + h.kbd("Ctrl+Shift+M") + " opens the "
                "“Ask Memory” window - it works outside calls too. “Search” "
                "mode returns relevant facts as a list: with the scope, a "
                "scope badge and the type. “Answer” mode hands the question "
                "to the LLM, but the model answers only from memory and "
                "shows sources. If memory has nothing, the window honestly "
                "writes “This is not in the saved memory” without calling "
                "the model. The finished answer in the “ANSWER” block is "
                "copied with the “Copy answer” button (the visible text "
                "without markup goes to the clipboard) or with " +
                h.kbd("Ctrl+C") + " after clicking the answer."),

            h.img("34-ask-memory.png",
                  "The “Ask Memory” window: the query, source selection and "
                  "found facts",
                  "wide"),

            h.h2("The pre-meeting brief"),
            h.p("When recording starts with memory enabled, a “Before the "
                "meeting” window pops up: the project, “Last call: N days "
                "ago”, current facts and open questions/deadlines for the "
                "project, an editable meeting plan, and two flags - “Read "
                "memory in this meeting” and “Remember this meeting”. "
                "“Start meeting” passes the plan into the session, “Skip” "
                "closes the window without a brief. If there were no past "
                "calls, the window says exactly that - “this is the first "
                "meeting”."),

            h.h2("The memory chip in the overlay"),
            h.p("In the bottom panel of the overlay a round memory indicator "
                "appears for the current meeting: gray - disabled, yellow - "
                "read-only or write-only, green - read and write. A click "
                "toggles the state for this meeting; the same is done by " +
                h.kbd("Ctrl+Shift+G") + ". The toggle is one-off: the "
                "global flags on the “Memory” tab are untouched."),

            h.h2("Retention and privacy"),
            h.ul([
                "“Transcript retention”: never / 7 / 30 / 90 days / until "
                "deleted manually - background cleanup ages the journals "
                "automatically.",
                "“Extracted memory retention”: no term, 30/90 days or a "
                "custom term in days - expired facts are removed by a "
                "background job.",
                "“Default project for new meetings”: the scope new facts "
                "fall into; “No project” writes into global memory. The "
                "“＋”/“✎”/“🗑” buttons next to the combo create, rename and "
                "delete projects right from settings (deletion - with "
                "confirmation: the project's facts are deleted together "
                "with it).",
            ]),
            h.callout("warn", "Deletion is real",
                      "“Delete” in the Memory Center erases the record from "
                      "memory.db with no recycle bin and no restore. If the "
                      "memory is dear - “Export…” to JSON first, then "
                      "delete."),
            h.callout("info", "What leaves the machine",
                      "The memory.db file is fully local. Only the "
                      "extraction prompt (when the “Memory model” is not "
                      "disabled) and texts for embeddings (when "
                      "“OpenAI-compatible API” is selected) go out. API "
                      "keys are absent from memory and from export by "
                      "design - they are stored in config.json, and only "
                      "facts and projects are exported."),
        ])),
    ]
