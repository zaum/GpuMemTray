# GPU Memory Tray

A small Windows tray application for monitoring NVIDIA GPU memory. Hovering over the icon shows a dark panel with total VRAM usage, a colored saturation bar, and a list of GPU processes sorted by memory usage.

![GPU Memory Tray screenshot](img/screenshot.jpg)

## Build and Run

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' publish -c Release
Start-Process .\bin\Release\net10.0-windows\publish\GpuMemTray.exe
```

The application uses the `nvidia-smi` command (installed by the NVIDIA driver) when available, and falls back to Windows' `GPU Process Memory` performance counter if it is not - also for the individual rows when the driver reports a value for only some of them. The right-click menu allows toggling the percentage icon and automatic Windows startup.

## Display scaling

The popup is as wide as its content needs - the longest application name, the memory value and the header decide it - and it is laid out in logical pixels scaled to the DPI of the monitor it opens on, so it keeps its proportions from 100% to 200% and higher. When Windows sends a DPI-change notification while the popup is open, the geometry and rows are rebuilt for the new scale.

## Note

In Windows WDDM mode, some versions of the NVIDIA driver do not return per-process VRAM usage via `nvidia-smi`. In such cases, the application reads dedicated VRAM from Windows' own `GPU Process Memory` counter.