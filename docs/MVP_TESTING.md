# SnapStack MVP testing

This checklist validates behavior that cannot be proven by compile-time CI alone: Windows activation routing, Snipping Tool callbacks, clipboard format negotiation, foreground focus, and input injection.

## Core capture flow

- Install the latest signed test artifact from the **Package** workflow.
- Launch SnapStack and press **S** to start the session.
- Confirm **Ctrl+Z** opens the rectangle Snipping Tool overlay.
- Capture at least three visually distinct regions in a known order.
- Confirm the capture count increments after every callback.
- Confirm SnapStack does not steal foreground focus after each Snipping Tool callback.
- Press **Ctrl+X** and confirm the session becomes ready to paste.

## Stack paste

In each target, press **Ctrl+V** once and record the result.

Expected behavior:

1. The target application remains foreground.
2. SnapStack releases its global **Ctrl+V** registration and waits for the physical shortcut keys to be released.
3. Each captured image is placed on the clipboard individually.
4. SnapStack injects one **Ctrl+V** per image in capture order.
5. After the sequence completes, the full multi-format clipboard payload is restored and the global **Ctrl+V** shortcut is registered again.

The paste path uses Win32 `SendInput`. Windows UIPI can block injected input when the target process runs at a higher integrity level than SnapStack; test elevated targets separately.

## Regression checks

- Starting a new session clears the previous capture collection.
- **Clear** disables all session hotkeys.
- **S** starts a session only while the SnapStack window is active.
- **Ctrl+Z** captures and **Ctrl+X** ends the active session globally.
- **Ctrl+V** behaves normally while a capture session is active and is intercepted only after **Stop**.
- Cancelling the Snipping Tool overlay does not add a capture.
- A second launch redirects to the existing SnapStack instance.
- A Snipping Tool protocol callback returns to the existing session rather than creating a new one.
- Clipboard publication preserves capture ordering.
- The full stack remains available and **Ctrl+V** is registered again after stack paste completes.

## Result log

| Target | All images pasted | Order correct | Repeated Ctrl+V works | Notes |
| --- | --- | --- | --- | --- |
| Microsoft Word | ☐ | ☐ | ☐ | |
| Browser rich-text editor | ☐ | ☐ | ☐ | |
| Notion | ☐ | ☐ | ☐ | |
| Discord / chat composer | ☐ | ☐ | ☐ | |
| File-oriented destination | ☐ | ☐ | ☐ | |
