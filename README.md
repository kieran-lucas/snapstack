# SnapStack

SnapStack is a Windows utility for capturing multiple screenshots into an ordered session and pasting the full stack with one paste action.

## MVP workflow

1. Launch SnapStack and select **Start session**.
2. Press **Ctrl+Shift+S** for each screenshot.
3. Select a rectangle in the Windows Snipping Tool overlay.
4. Repeat as many times as needed.
5. Select **Stop**.
6. Switch to the target app and press **Ctrl+V** once.

SnapStack republishes the full ordered stack to the Windows clipboard after every successful capture and once more when the session stops.

## Current architecture

- C# / .NET 10
- WinUI 3 + Windows App SDK 2.5.1
- MSIX package identity
- Windows Snipping Tool protocol for rectangle capture
- Windows App SDK single-instance activation routing
- Win32 `RegisterHotKey` for the session-scoped capture shortcut
- Multi-format clipboard payload:
  - RTF image stack
  - HTML image stack
  - multiple PNG files
  - bitmap fallback
- GitHub Actions build and signed test-package workflows

## Install the current test build

The **Package** GitHub Actions workflow produces a temporary x64 test artifact named:

`SnapStack-win-x64-test`

To test it:

1. Open **Actions → Package** in this repository.
2. Open the latest successful run and download `SnapStack-win-x64-test`.
3. Extract the ZIP.
4. Open the generated `packageSnapStack_*_x64_Test` folder.
5. Run `Install.ps1` with PowerShell and follow the Windows prompts.
6. Launch **SnapStack** from the Start menu.

The package is signed with a CI-generated self-signed certificate intended only for development/testing. The workflow exports only the public `.cer`; the private signing key is deleted from the runner before the artifact is uploaded.

## Development

Every implementation block is committed atomically to `main`. GitHub Actions verifies the Windows build after each push.

### Build locally

```powershell
dotnet restore src/SnapStack/SnapStack.csproj -p:Platform=x64
dotnet build src/SnapStack/SnapStack.csproj --configuration Release -p:Platform=x64 --no-restore
```

## Status

MVP integration is in progress. Capture sessions, rectangle capture, global capture hotkey, single-instance callback routing, and multi-format clipboard publishing are implemented.
