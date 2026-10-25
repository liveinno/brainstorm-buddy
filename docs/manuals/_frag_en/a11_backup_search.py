# -*- coding: utf-8 -*-
"""User guide fragment: profile backup and transfer, search across
meetings/memory/settings, questions about PDF pages and the whole PDF,
and the AI meeting summary. Version 2.32.0 functionality."""


def chapters(h):
    return [
        ("Search across meetings and memory", "".join([
            h.p("Since version 2.32.0 search merges the meeting history and "
                "Buddy Memory. A query runs over two indexes at once: exact "
                "textual (words as written) and semantic, over E5 embeddings "
                "(the question in other words). Results are ranked together, "
                "so both the exact phrase and a rephrased one are found."),

            h.h2("Where to search"),
            h.ul([
                "<b>“Ask Memory”</b> (the memory window) - the source selection "
                "sits on top: “Memory and meetings” (default), “Memory only”, "
                "“Meetings only”. The answer contains the found information, "
                "the meeting date and a clickable source.",
                "<b>The left search field in the meeting history</b> - results "
                "over all saved meetings without the “last N” limit. A click "
                "on a result opens the meeting and highlights the needed line "
                "with a golden background (visible in both themes).",
                "<b>Search inside an open transcript</b> - Ctrl+F in the "
                "meeting window: a search field, a “2 of 7” counter, Enter - "
                "the next match, Shift+Enter - the previous one. If the match "
                "is in the AI summary, the jump leads to the summary with the "
                "source noted.",
            ]),

            h.img("43-search-meetings.png",
                  "Search over the meeting history: the query and found lines",
                  "wide"),

            h.h2("What the answer shows"),
            h.p("Each result is labeled by type: participants' words, an AI "
                "answer, or an AI summary. An AI answer from a past chat is "
                "never passed off as a participant's words - the label makes "
                "that clear. A click on the source opens the history at the "
                "exact line."),
            h.p("If the semantic index is still being built (first launch or "
                "a large update), exact search works right away, and the "
                "status shows partial readiness. On timeout the app honestly "
                "reports “search not finished - try again” instead of "
                "returning an empty answer."),
        ])),

        ("Backup and transfer", "".join([
            h.p("The “Backup and transfer” settings section packs the whole "
                "profile into a single password-protected archive and deploys "
                "it on another computer or into a clean profile."),
            h.img("38-tab-backup.png",
                  "The “Backup” section in settings: an archive under a "
                  "password",
                  "wide"),

            h.h2("What goes into the archive"),
            h.ul([
                "memory (facts, decisions, tasks) and its change history;",
                "meetings, transcripts and AI summaries with source links;",
                "attachments and images with the original files;",
                "settings, user prompts and LLM profiles;",
                "saved secrets - in a portable form (see below about the "
                "password).",
            ]),
            h.p("Before creation you see an inventory of the stores and a size "
                "estimate for the archive. Downloaded models are NOT included "
                "in the archive by default: the “Exclude downloaded models” "
                "checkbox is ticked, and at the new place the models are "
                "re-downloaded. For a full transfer together with models - "
                "untick this box (that is gigabytes). User data cannot be "
                "excluded. Archives created before this setting are restored "
                "whole, together with the models directory."),

            h.h2("Creating a backup"),
            h.p("“Create backup” → choose a file → password. The password is "
                "needed precisely for the secrets: API keys are packed so "
                "that they open on another computer without being tied to the "
                "old Windows account (the old PC's DPAPI is not required). "
                "The database snapshot is taken consistently - a running "
                "SQLite with WAL is copied correctly. Progress and a stop "
                "button are visible while packing."),

            h.h2("Restore"),
            h.p("“Restore from archive” → file → password. Format, version "
                "and integrity are checked first; the archive contents and a "
                "warning about replacing the current profile are shown. The "
                "data being replaced backs itself up, restore runs in a "
                "temporary directory and is applied atomically - on an error "
                "or a wrong password the original profile stays untouched. "
                "The archive cannot write files outside the restore "
                "directory."),
            h.p("Paths inside the profile are remapped to the new location. "
                "External files missing on the new PC (MCP tool exes, model "
                "directories, the saves folder) are shown in the report as "
                "“Missing” but do not cancel restoring the rest. After the "
                "restore you see the same meetings, facts, documents and "
                "settings; source links work, and the meeting search index "
                "refreshes automatically on the first search - while it is "
                "being built, this is visible in the status."),
        ])),

        ("Documents: questions about pages and the whole PDF", "".join([
            h.p("A PDF attached to a meeting now answers both targeted and "
                "general questions. The document status shows the page "
                "breakdown: “Ready: 350/350 pp. · OCR: 2 pp.” - how many "
                "pages in total, how many were read from the text layer, how "
                "many went through OCR and how many are left without a result "
                "(with their numbers)."),
            h.img("39b-doc-picker-file.png",
                  "The “Files for context” window: the selected document, the "
                  "PDF mode and the OCR mode",
                  "wide"),

            h.h2("A question about a page"),
            h.p("“What is written on page 87?” - the number is treated as a "
                "PDF page address (numbering from 1), not as a search word. "
                "Ranges are supported (“pp. 5-8”, “3 to 6”, “pages 2-4”) as "
                "well as the document tail (“the last 3 pages”, “end of "
                "document”). If the book's printed numbering does not match "
                "the PDF pages, the PDF number is used - this is noted in the "
                "answer."),

            h.h2("A question about the whole document"),
            h.p("A general question (“what is this document about”) launches "
                "a staged summarization of the whole PDF: the document is "
                "read in parts with progress and a stop button. This is not a "
                "“sample of the beginning, middle and end” - the summary "
                "covers the whole file. Fragments for the query are selected "
                "to fit the model's context with headroom for the answer, so "
                "large documents do not hit the limit."),
            h.img("40-document-qa.png",
                  "The answer on an attached document in the chat: the "
                  "question, the answer, and the finished file in the "
                  "attachments list",
                  "wide"),

            h.h2("A tip"),
            h.p("With several attached documents, name the one you mean in "
                "the question (“in the supply contract…”) - otherwise the "
                "assistant will ask which document you are talking about."),
        ])),

        ("Search in settings", "".join([
            h.p("The search line at the top of the settings window searches "
                "parameter names, sections and descriptions - including "
                "synonyms: “llm”, “model”, “tokens”, “ocr”, “memory”, "
                "“backup”. API key values and other secrets never get into "
                "the search."),
            h.img("41-settings-search.png",
                  "Search in settings: matches by parameter names and "
                  "sections",
                  "wide"),
            h.p("A click on a result opens the right section, scrolls to the "
                "parameter and briefly highlights it. Search changes nothing "
                "by itself: edits you typed are preserved after closing "
                "search. If there are no matches you see the “Nothing found” "
                "state."),
        ])),

        ("AI meeting summary", "".join([
            h.p("The “AI meeting summary” button builds the summary from parts "
                "of the transcript: the current stage and progress are "
                "visible, and there is a stop button. For reasoning models a "
                "thinking budget is reserved, so the answer is not cut off "
                "mid-thought and does not show raw reasoning instead of the "
                "result."),
            h.img("42b-meeting-summary.png",
                  "AI meeting summary: the summary is built and shown under "
                  "the transcript",
                  "wide"),
            h.p("On an error or cancellation the already finished parts are "
                "kept and only unfinished ones repeat - the previous summary "
                "is not erased. The result is bound to the meeting: switching "
                "windows and restarting the app do not lose it."),
        ])),
    ]
