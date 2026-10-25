def chapters(h):
    return [
        ("Images in the chat", "".join([
            h.p("In the “Send secret message” field on the overlay you can paste pictures and ask "
                "the model about them — for example, “what is on this screenshot” or “translate the text from the frame”. "
                "The image is sent to the model as a picture (for vision models), not as "
                "recognized text."),

            h.h2("How to paste a picture"),
            h.ul([
                "Ctrl+V — pastes an image from the clipboard straight into the input field.",
                "Right-click on the field → “Paste image” (the same thing).",
                "Works both for an image file and for a screenshot in the clipboard (a bitmap from the clipboard "
                "is converted to PNG automatically).",
            ]),
            h.p("As long as there is an unsaved draft, pasted pictures are shown as a strip of "
                "thumbnails above the input field. Limits: up to 4 images per message, an original "
                "up to 25 MiB, the whole draft up to 100 MiB; PNG, JPEG, BMP formats."),
            h.img("31-chat-draft.png",
                  "The draft strip: a pasted picture above the “Send secret message” field",
                  "medium"),

            h.h2("Viewing and managing"),
            h.ul([
                "Clicking a thumbnail (in the draft, in a chat bubble or in history) opens the shared "
                "viewer: “Fit”, “100%”, zooming, arrows across the message's images, "
                "Esc or the cross — close.",
                "In an answer bubble images are shown as tiles up to 320×240, at most two columns.",
                "Right-click on a picture — “Save as...” and “Copy image”.",
                "Originals are stored in attachments\\images\\img-<GUID>\\ — the image "
                "survives a restart and can be opened from history.",
            ]),

            h.h2("Does the model support images"),
            h.p("Buddy checks whether the selected model can accept images. "
                "The service caches the answer per the “provider + address + model” combination and lets you "
                "run a probe with a synthetic picture; changing the API key clears the cache. "
                "If the model does not accept images, Buddy will honestly say so instead of "
                "dropping the request."),

            h.callout("info", "A picture together with tools",
                      "If MCP tools are connected to the chat, the model can in a single answer "
                      "both look at the picture and call a tool — the image is not lost "
                      "along the way."),
        ])),
    ]
