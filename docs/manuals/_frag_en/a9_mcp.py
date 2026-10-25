# -*- coding: utf-8 -*-
def chapters(h):
    return [
        ("Connecting AI agents and tools (MCP)", "".join([
            h.p("The “MCP” tab in settings links Buddy to external programs via the "
                "MCP protocol (Model Context Protocol). The link works in both directions: "
                "another agent on the same computer can ask Buddy about your "
                "meetings and memory (an incoming connection), and Buddy itself can call the "
                "tools of third-party MCP servers straight from the chat (an outgoing "
                "connection)."),
            h.img("30-tab-mcp.png",
                  "The “MCP” tab in settings: cards for the incoming and outgoing connections",
                  "wide"),

            h.h2("What “incoming” and “outgoing” mean"),
            h.table(
                ["Direction", "What it means", "Who needs it"],
                [
                    ["Incoming", "Buddy itself becomes an MCP server and accepts requests from "
                     "other programs (for example, Claude Desktop). They can read your "
                     "meetings, memory and attachments within the granted permissions.",
                     "Give an assistant access to Buddy's data"],
                    ["Outgoing", "Buddy connects to third-party MCP servers and calls their "
                     "tools from the chat: for example, “look up the task in the tracker”.",
                     "Give Buddy access to external services and databases"],
                ]),
            h.p("Each direction has its own connection wizard: the “Connect agent” button "
                "opens the incoming wizard, “Add tools” — the outgoing one. "
                "The step-by-step guide for Codex is in the chapter “Connecting BrainstormBuddy "
                "to Codex”."),

            h.h2("The incoming wizard: “Connect agent”"),
            h.img("45-mcp-wizard.png",
                  "The agent connection wizard: step 1 of 4 — choosing the application",
                  "wide"),
            h.p("The step-by-step wizard grants an external agent access to Buddy's data:"),
            h.ul([
                "Step 1 — application and transport: Claude Desktop, Claude in the cloud or "
                "“Another agent”; on this computer (named pipe) or “In the cloud” "
                "(public HTTPS through a tunnel).",
                "Step 2 — access: data scope (all projects, existing ones only, "
                "selected meetings) and permissions (read data, add facts, modify, "
                "delete, enable memory).",
                "Step 3 — connection: the connection name and the “Advanced” block with a ready-made configuration — the launch command and the path to the token file. For "
                "Claude Desktop Buddy can write it straight into the client's file; for "
                "other clients the values are transferred manually.",
                "Step 4 — verification: Buddy raises a local host and waits for the client "
                "to connect.",
            ]),
            h.p("The token is stored encrypted (DPAPI) under your account. Every "
                "agent action is checked against the granted permissions — the list of connections "
                "and their status (Connected / Setup ready / No connection / Access revoked) is visible on this same tab; any token can be revoked with a button."),

            h.h2("Connection permissions and working with memory"),
            h.p("You grant the permissions — on the “Access” step. Every agent action is checked "
                "against them, and a denial arrives as an error, not an empty answer."),
            h.table(
                ["Permission", "What it allows the agent to do"],
                [
                    ["Read data", "Search and read meetings, memory and attachments"],
                    ["Add facts", "Write new facts into Buddy's memory"],
                    ["Modify data", "Edit the text of an already saved fact"],
                    ["Delete", "Mark a fact as deleted — and only after your "
                     "confirmation in the Buddy overlay"],
                    ["Enable memory", "Grants access to Buddy's memory; without this flag "
                     "the memory tools do not work"],
                ]),
            h.p("The checkboxes are independent. Adding a fact works even without the read permission: "
                "the agent does not need to see your data to write a fact. If you clear "
                "all the checkboxes, the connection survives with no powers at all — Buddy does not "
                "expand permissions on its own, and the agent will read and change nothing."),
            h.p("The agent works with memory through three tools:"),
            h.ul([
                "buddy_add_fact — save a fact (the same text returns the already existing "
                "fact instead of creating a second one).",
                "buddy_update_fact — edit the text of a fact by its id (the id comes from the "
                "buddy_add_fact response or from search).",
                "buddy_delete_fact — delete a fact by its id: both the “Delete” permission "
                "and your confirmation are required.",
            ]),
            h.callout("warn", "Deletion is always yours",
                      "The agent can delete a fact only with the “Delete” permission and only "
                      "after you confirm the deletion with a card in the Buddy "
                      "overlay. A denial, a cancellation or silence for 5 minutes — no deletion "
                      "happens, the fact stays in place. The deletion itself is soft: "
                      "the fact is marked deleted, the record is not erased."),

            h.h2("The host path: permanent, not temporary"),
            h.p("The ready-made configuration points to the installed copy of the host:"),
            h.code("%LOCALAPPDATA%\\Programs\\BrainstormBuddy\\BrainstormBuddy.McpHost.exe"),
            h.p("Temporary and working paths (the single-file extraction folder, working "
                "bin directories and so on) never get into the config: after a reboot or "
                "a reinstall the path keeps working. The token itself is not passed on the command line — the host reads the token file, available only to you."),

            h.h2("Cloud access"),
            h.p("For an agent on another computer Buddy can raise a public "
                "HTTPS endpoint through a tunnel. The primary route is Tailscale Funnel: if "
                "Tailscale is installed, the wizard enables Funnel by itself and shows the ready "
                "address. If there is no tunnel, the wizard offers a manual option (ngrok, "
                "cloudflared and so on) and waits until you provide a public address already "
                "pointing at this computer. Without an installed tunnel the tab honestly shows instructions — the cloud endpoint will not turn on “silently”."),

            h.h2("The outgoing wizard: “Add tools”"),
            h.p("The wizard connects a third-party MCP server (a stdio command or an HTTP address). "
                "After connecting, its tools appear in the chat, and Buddy calls them "
                "inside its answers. Every call first passes your control:"),
            h.ul([
                "A tool-call request shows the server, the tool name and "
                "the arguments being passed — you can approve or deny.",
                "Dangerous actions are tagged with a risk level; without approval they are not executed.",
                "Step and budget limits constrain the chain of calls; a long task can be "
                "paused and continued later.",
            ]),
            h.p("If the server requires authorization (for example, a cloud YouTrack), "
                "the wizard has a “Token” field — paste the secret token issued by the server. The value goes into the Windows protected store, and the "
                "config.json only gets a reference to it: the token in plain text is "
                "written neither to settings nor to logs."),
            h.ul([
                "The “Check” button and a real tool call from the chat "
                "authorize identically: a successful check is exactly the request that will later go from the chat.",
                "On failure the reason is shown: 401 (the token was rejected by the server), "
                "403 (access denied), the server is unreachable or did not answer in time.",
                "The token is entered once: when editing the connection the saved "
                "value stays unchanged until you enter a new one.",
            ]),
            h.callout("info", "The model picks the tools",
                      "After connecting there is no need to enumerate tools and "
                      "parameters manually: describe the task in a plain sentence and the model "
                      "itself picks the right tool of the connected server. "
                      "The call is still shown to you for confirmation."),
            h.callout("info", "Status only by fact",
                      "The “Connected” status appears only after a real handshake and "
                      "a successful request to the server, not because a row exists in the settings — there are no spurious “green” checkmarks."),

            h.h2("MCP check and logs"),
            h.p("The “MCP” tab has a “Check MCP” button. It does not guess — it "
                "collects the actual state: which channel is waiting for a connection, "
                "which permissions and data scope the token was granted, where the meeting and memory data live, which tools are available to the agent. The report opens "
                "right in the settings window, and next to it there is a “Log…” button — it opens "
                "the folder with the logs."),
            h.p("There are two logs, and their entries are linked by a common request identifier:"),
            h.ul([
                "mcp.log — the Buddy side: who connected, what was requested, with which "
                "token, with which permissions, how long a call took.",
                "mcphost.log — the external agent side (McpHost): the same calls seen "
                "from the other side.",
            ]),
            h.p("The log shows everything people usually ask about: the handshake, "
                "the permission check, the call itself with parameters, the answer, the duration, a user cancellation, a timeout and a dropped connection. If a call failed with "
                "an error, the exception type and its text are written next to it, and by the "
                "request identifier the corresponding log line can be found. "
                "The tokens themselves never get into the log — they are masked."),
            h.p("The “Copy instructions for AI” button puts a ready-made system prompt for an agent into the clipboard: which tools are available, which "
                "parameters they have and how the paginated output works. This is handy when connecting a new agent and you don't want to describe the tools "
                "manually."),
            h.callout("info", "A new token works immediately",
                      "After “Connect agent” the issued token and new permissions "
                      "apply immediately — there is no need to switch MCP off and on again. The previous token stops working, and an agent using it "
                      "gets a clear denial with a request identifier."),

            h.h2("If the wizard does not open or the server is silent"),
            h.ul([
                "The wizard opens even with no servers configured — there must be no crash. "
                "If the window did not appear, check the log (Ctrl+Shift+L).",
                "For an incoming connection check that the agent was given a correct token and the required permissions have not been revoked.",
                "For cloud access make sure the tunnel (Tailscale/ngrok) is installed, logged into the account and the address really points at this computer.",
                "If the agent connects but the calls fail — open “Check MCP” "
                "and the mcp.log / mcphost.log logs: they show at which step of the chain "
                "(handshake, permissions, call) the break happened.",
            ]),
        ])),
    ]
