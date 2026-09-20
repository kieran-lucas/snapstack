# SnapStack MVP testing

This checklist validates behavior that cannot be proven by compile-time CI alone: Windows activation routing, Snipping Tool callbacks, clipboard format negotiation, foreground focus, and input injection.

## Core capture flow

- Install the latest signed test artifact from the **Package** workflow.
- Launch SnapStack and select **Start session**.
- Confirm **Ctrl+Shift+S** opens the rectangle Snipping Tool overlay.
- Capture at least three visually distinct regions in a known order.
- Confirm the capture count increments after every callback.
- Confirm SnapStack does not steal foreground focus after each Snipping Tool callback.
- Select **Stop** and confirm the session becomes ready to paste.

## Native paste

In each target, press **Ctrl+V** once and record the result.

| Target class | Expected result |
| --- | --- |
| Rich-text editor | All captures appear in sequence from the RTF or HTML representation. |
| Web editor | All captures appear in sequence when the editor accepts the HTML representation. |
| File-oriented target | The ordered PNG files are offered through the multi-file representation. |
| Bitmap-only target | At minimum, the first capture is available as the bitmap fallback. |

Native paste is the preferred path because the destination application chooses the richest clipboard format it supports.

## Compatibility paste

Use **Ctrl+Shift+V** only when native paste does not insert the full stack.

Expected behavior:

1. The target application remains foreground.
2. SnapStack waits for the shortcut modifier keys to be released.
3. Each captured image is placed on the clipboard individually.
4. SnapStack injects one **Ctrl+V** per image in capture order.
5. After the sequence completes, the full multi-format SnapStack clipboard payload is restored.

The compatibility path uses Win32 `SendInput`. Windows UIPI can block injected input when the target process runs at a higher integrity level than SnapStack; test elevated targets separately.

## Regression checks

- Starting a new session clears the previous capture collection.
- **Clear** disables both session hotkeys.
- Cancelling the Snipping Tool overlay does not add a capture.
- A second launch redirects to the existing SnapStack instance.
- A Snipping Tool protocol callback returns to the existing session rather than creating a new one.
- Clipboard publication preserves capture ordering.
- The full stack remains available after compatibility paste completes.

## Result log

| Target | Native Ctrl+V | Fallback Ctrl+Shift+V | Order correct | Notes |
| --- | --- | --- | --- | --- |
| Microsoft Word | ☐ | ☐ | ☐ | |
| Browser rich-text editor | ☐ | ☐ | ☐ | |
| Notion | ☐ | ☐ | ☐ | |
| Discord / chat composer | ☐ | ☐ | ☐ | |
| File-oriented destination | ☐ | ☐ | ☐ | |
