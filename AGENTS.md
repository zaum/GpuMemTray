# Agent Guidelines & Workflow Rules

Whenever code modifications are made to this project (`GpuMemTray`), the AI
agent MUST perform the following post-build workflow:

## 1. Terminate Running Instances

Stop any currently running `GpuMemTray` process to release file locks (the
`apphost.exe` copy in step 2 fails with MSB3021 while the old binary is
still running):

```powershell
Stop-Process -Name "GpuMemTray" -Force -ErrorAction SilentlyContinue
```

## 2. Build and Publish Release Executable

Publish the Release version using the 64-bit .NET SDK. Always call the full
path: the `dotnet.exe` first on PATH is the 32-bit stub, which has no SDK
installed and fails with "No .NET SDKs were found".

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish -c Release
```

## 3. Launch the Application

Launch the newly published executable from the publish directory so it runs
active in the system tray:

```powershell
Start-Process "I:\APPLICATIONS\GPU processes\GpuMemTray\bin\Release\net10.0-windows\publish\GpuMemTray.exe"
```

## UI & Layout Standards

### Content-Fitted Width

The popup window is only as wide as its content needs: the widest process row
(name + value + kill button), the header (title + usage) or the "no data"
message, clamped to a 300-560px range. Never hardcode the width.

Two traps make this easy to get wrong:

- Measure on a surface pinned to the current DPI scale factor (see
  `MeasuringSurface`), not on the form's own `Graphics`. A hidden form reports
  the primary monitor's DPI, so it would size the popup for the wrong screen.
- Set every label's text *before* measuring. `ApplyLayout` measures what is on
  screen right now, not what is about to be set.

All pixel literals are logical (96 dpi) values multiplied by `DpiScaling.Scale`.
Writing raw pixels instead leaves 200% displays with 420 physical pixels - half
the logical width - and the text no longer fits.

### Text Truncation

Long application names must be truncated using `AutoEllipsis = true`
(displaying `...`) and `UseMnemonic = false`.

Custom-painted text (`Graphics.DrawString`) is not a Label and does not
honour `AutoEllipsis`: its `StringFormat` needs `NoWrap` plus a `Trimming`
value. Without them GDI+ word-wraps an overlong value and the `LineLimit`
flag inherited from `StringFormat.GenericDefault` silently drops the
overflow line, so the text renders truncated with no ellipsis.

Give the value column exactly its measured width and let the name take the
remainder - never a fixed split, or the unit is what gets squeezed out.

### Dynamic Height

Popup height must dynamically adapt to the number of active GPU processes
without clipping or unnecessary scrollbars.

### Always On Top

The popup must stay above every other window, not merely be marked topmost.
`WS_EX_TOPMOST` only orders a window inside the topmost band, so another
topmost window (overlay, floating panel, shell flyout) raised later still
covers the popup. `PopupWindow.EnsureAboveAll` re-raises it with
`SetWindowPos(HWND_TOPMOST, ...)` whenever a foreign window sits above it, and
every show ends with an explicit raise.

Never drop `SWP_NOACTIVATE`: raising the z-order must not steal focus from the
window the user is working in. Windows of this process (the row tooltips, the
context menu) and windows that do not overlap the popup's body (the taskbar)
are skipped by the z-order check, so they never trigger a raise.

### Tray Hover Zone

The popup touches the taskbar and the tray icons are in that strip, so the
keep-open tolerance (`ContainsCursor`) must not cross the taskbar edge on the
arrow's side - the neighbouring icons would count as hovering the popup and it
would never close once the cursor slid along the tray. Only a narrow corridor
over our own icon cell may enter that strip, to bridge the gap between the
arrow and the icon. Because the popup now stays reliably on top, such a stale
tolerance is no longer hidden behind the taskbar.

Keep the icon tolerances tight for the same reason: `IsOverTrayIcon` inflates
the shell-reported icon rect by 8 logical pixels and the shell-hover fallback
uses a 10 pixel radius. The neighbouring icons sit about one icon gap (+-16 px)
away, so the previous 16 pixel values reached them and made the popup appear
over - or stay open on - the icons on either side.

### Spacious Row Padding

Process row height must be spacious so rows never overlap or look cramped.

### Clean Tray Popups

Keep `NotifyIcon.Text` empty (`string.Empty`) so native OS tray hover tooltips
do not appear alongside the custom popup window.
