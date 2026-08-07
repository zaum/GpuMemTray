# Agent Guidelines & Workflow Rules

Whenever code modifications are made to this project (`GpuMemTray`), the AI
agent MUST perform the following post-build workflow:

## 1. Terminate Running Instances

Stop any currently running `GpuMemTray` process to release file locks:

```powershell
Stop-Process -Name "GpuMemTray" -Force -ErrorAction SilentlyContinue
```

## 2. Build and Publish Release Executable

Publish the Release version using the 64-bit .NET SDK:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish -c Release
```

## 3. Launch the Application

Launch the newly published executable from the publish directory so it runs
active in the system tray:

```powershell
Start-Process "i:\GPU processes\GpuMemTray\bin\Release\net8.0-windows\publish\GpuMemTray.exe"
```

## UI & Layout Standards

### Fixed Width

The popup window must maintain a fixed width (420px).

### Text Truncation

Long application names must be truncated using `AutoEllipsis = true`
(displaying `...`) and `UseMnemonic = false`.

### Dynamic Height

Popup height must dynamically adapt to the number of active GPU processes
without clipping or unnecessary scrollbars.

### Spacious Row Padding

Process row height must be spacious so rows never overlap or look cramped.

### Clean Tray Popups

Keep `NotifyIcon.Text` empty (`string.Empty`) so native OS tray hover tooltips
do not appear alongside the custom popup window.
