# -*- coding: utf-8 -*-


def chapters(h):
    return [
        ("Connecting BrainstormBuddy to Codex", "".join([
            h.p("Buddy can hand meeting and memory data to an external agent "
                "over the MCP protocol. Below - connecting Codex on the same "
                "computer: Buddy acts as the MCP server, Codex as the client. "
                "The exchange is local: only what you allow at the “Access” "
                "step leaves the machine."),

            h.h2("Step 1. In Buddy: create the connection"),
            h.p("Open “Settings” (Ctrl+Shift+S) and go to the “MCP” section." +
                " Press “Connect AI agent” - a four-step wizard opens."),
            h.img("46-buddy-mcp-tab.png",
                  "The “MCP” section in Buddy settings: the “Connect AI "
                  "agent” button",
                  "wide"),

            h.p("Wizard step 1 - “App”. Choose “Other agent” (there is no "
                "ready-made entry for Codex in the list) and leave “On this "
                "computer”: Codex launches the Buddy host locally, nothing is "
                "opened to the outside."),
            h.img("47-buddy-wizard-step1.png",
                  "Wizard step 1 - “App”: “Other agent” and “On this "
                  "computer”",
                  "wide"),

            h.p("Step 2 - “Access”. Choose the scope (“All projects, "
                "including new ones”) and the rights: for reading data the "
                "“Read data” checkbox is enough, “Include memory” adds access "
                "to Buddy's memory. Modification and deletion rights are not "
                "needed if the agent only reads."),
            h.img("48-buddy-wizard-step2.png",
                  "Wizard step 2 - “Access”: the data scope and the agent's "
                  "rights",
                  "wide"),

            h.p("Step 3 - “Connect”. Enter the connection name (for example, "
                "“Codex”) and expand the “Advanced” block: there is the ready "
                "configuration - the launch command and the token file path. "
                "These two values are carried over to Codex at the next "
                "step."),
            h.img("49-buddy-wizard-step4-config.png",
                  "Wizard step 3 - “Connect”: the “Advanced” block with the "
                  "command and the token file path",
                  "wide"),
            h.callout("warn", "Take the paths from your own wizard",
                      "Copy the command, the argument and the token file path "
                      "from the “Advanced” block of your own Buddy wizard. The "
                      "“Argument” field needs the path to the token file (for "
                      "example C:\\Users\\<name>\\AppData\\Roaming\\"
                      "BrainstormBuddy\\mcp\\tokens\\<identifier>.token), not "
                      "the token text itself. Paths from someone else's "
                      "machine will not work - you have different folders and "
                      "a different token."),
            h.p("The command in the config points to the installed copy of "
                "the host - the permanent path "
                "%LOCALAPPDATA%\\Programs\\BrainstormBuddy\\BrainstormBuddy.McpHost.exe, "
                "not the temporary unpack folder. If the “Advanced” block "
                "shows a different path, set Codex up again - otherwise the "
                "host will not start after a reboot."),

            h.p("Wizard step 4 - “Check”: Buddy raises the local host and "
                "waits for the client. Press “Connect”, return to the "
                "settings and save them with the “Save” button - the "
                "connection card will appear on the “MCP” tab."),
            h.img("50-buddy-connection-card.png",
                  "The “Codex” connection card on the “MCP” tab: the status "
                  "and the time of the last call",
                  "wide"),

            h.h2("Step 2. In Codex: add the Buddy server"),
            h.p("Open the Codex settings (Ctrl+,) and find the MCP section - "
                "in the current version it is “Plugins”."),
            h.img("51-codex-mcp-settings.png",
                  "Finding the MCP section in the Codex settings",
                  "wide"),

            h.p("The “MCP” tab shows the server list. Press “Add” and choose "
                "“Add MCP server”."),
            h.img("52-codex-mcp-before-add.png",
                  "The “MCP” tab in Codex: the server list and the “Add” "
                  "button",
                  "wide"),
            h.img("53-codex-add-mcp-menu.png",
                  "The “Add” menu: the “Add MCP server” item",
                  "wide"),

            h.p("Fill in four fields in the server form:"),
            h.table(
                ["Field", "What to enter"],
                [
                    ["Name", "Your own server name, for example "
                             "brainstorm-buddy"],
                    ["Type (transport)", "STDIO - the server runs as a local "
                                         "program"],
                    ["Command", "The path to BrainstormBuddy.McpHost.exe from "
                                "the “Advanced” block of your Buddy wizard - "
                                "usually the permanent path "
                                "%LOCALAPPDATA%\\Programs\\BrainstormBuddy\\"
                                "BrainstormBuddy.McpHost.exe"],
                    ["Argument", "The path to the token file (the same as in "
                                 "“Advanced”). The token itself does not go "
                                 "here"],
                ]),
            h.img("54-codex-stdio-form.png",
                  "The STDIO server form in Codex: the name, the command and "
                  "the argument",
                  "wide"),
            h.p("Save the form and refresh MCP - Codex has a refresh button "
                "next to the “Servers” heading. After the refresh the server "
                "appears in the list enabled."),
            h.img("55-codex-save-server.png",
                  "The saved server form: the required fields are filled",
                  "wide"),
            h.img("56-codex-server-enabled.png",
                  "The Buddy server added to Codex and enabled",
                  "wide"),

            h.h2("Step 3. Check: the meeting list, search, memory read and "
                 "write"),
            h.p("After the MCP refresh Codex sees the Buddy tools. You can "
                "verify the link with calls - from the simplest to the most "
                "informative:"),
            h.ol([
                "buddy_status - a service call: it answers that Buddy is "
                "running and shows the version.",
                "buddy_list_meetings - the list of your meetings (returned "
                "page by page).",
                "buddy_search - search over meetings and memory.",
                "buddy_read_source - reading a source: a fragment of a "
                "meeting or memory by the identifier from the previous "
                "step.",
                "buddy_add_fact - save a fact into Buddy's memory (needs the "
                "“Add facts” right).",
                "buddy_update_fact - change the text of a saved fact by its "
                "id (needs the “Change data” right).",
                "buddy_delete_fact - delete a fact by its id: needs the "
                "“Delete” right and confirmation with a card in the Buddy "
                "overlay. A refusal, a cancellation, or a 5-minute timeout - "
                "the fact stays in place.",
            ]),
            h.callout("info", "You grant the rights",
                      "Connection rights are set in the Buddy wizard at the "
                      "“Access” step. An empty set of rights is saved as is "
                      "and never expands on its own: untick all the boxes - "
                      "the agent will read and change nothing. Adding a fact "
                      "works even without the read right, and deletion "
                      "always waits for your confirmation in the Buddy "
                      "overlay."),
            h.callout("info", "The status appears after the fact",
                      "The connection card in Buddy switches to “Connected” "
                      "only after the first real authorized call and shows "
                      "the time of the last call. Saving the settings by "
                      "itself does not count as a connection: until the "
                      "agent has called at least once, the card keeps "
                      "“Waiting for the agent to connect”."),

            h.h2("Step 4. Logs and errors by request ID"),
            h.p("If a call failed, look at the logs: there are two of them, "
                "and their entries are linked by a common request ID."),
            h.ul([
                "mcp.log - the Buddy side: who connected, what they "
                "requested, with which rights, how long the call took.",
                "mcphost.log - the external agent side (McpHost): the same "
                "calls from the other side.",
            ]),
            h.p("The “Logs…” button on the “MCP” tab opens the log folder "
                "(the general app log - Ctrl+Shift+L). The log shows the "
                "handshake, the rights check, the call itself with "
                "parameters, the response and the duration; on an error the "
                "exception type and its text are written next to it. Copy "
                "the request ID from the agent's answer and find it in "
                "mcp.log - from it you can see at which step of the chain "
                "(handshake, rights, call) the break happened. The tokens "
                "themselves never get into the log - they are masked."),

            h.h2("Step 5. Revoking the connection"),
            h.p("The connection card has a “Revoke” button. Buddy will ask "
                "for confirmation and show which connection is losing "
                "access."),
            h.ul([
                "Cancel in the dialog - the connection stays valid, the "
                "token keeps working.",
                "Confirm - the token is deleted, the card disappears, and a "
                "call with the old token is rejected with a clear error. "
                "Buddy itself keeps working.",
            ]),
            h.p("A revoked token cannot be restored - if access is needed "
                "again, create a new connection via “Connect AI agent” and "
                "enter the new paths in Codex."),

            h.h2("If something did not work"),
            h.ul([
                "Buddy is not running - the local host does not start, the "
                "card stays “Setup complete”. Start Buddy and save the "
                "settings again.",
                "The token is revoked or re-issued - the agent will get a "
                "refusal with a request ID; create a new connection.",
                "The command points to a remote or old copy of Buddy - "
                "recreate the connection to take the current paths from the "
                "wizard.",
                "The server is added to Codex but no tools are visible - "
                "refresh MCP (restart the chat) and check that it is enabled "
                "in the server list.",
            ]),
        ])),
    ]
