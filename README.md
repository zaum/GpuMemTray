# GPU Memory Tray

A small Windows tray application for monitoring NVIDIA GPU memory. Hovering over the icon shows a dark panel with total VRAM usage, a colored saturation bar, and a list of GPU processes sorted by memory usage.

![GPU Memory Tray screenshot](img/screenshot.jpg)

## Build and Run

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' publish -c Release
Start-Process .\bin\Release\net10.0-windows\publish\GpuMemTray.exe
```

The application uses `nvidia-smi` (installed by the NVIDIA driver) for total GPU memory. In WDDM mode the driver can return `[N/A]` for per-process memory; in that case the app can show Windows `GPU Process Memory` values as white estimates prefixed with `~`. Windows can over-report these values, so they never affect the GPU-wide total. An estimate above the GPU's total memory is marked with `!`. The right-click menu allows toggling the percentage icon and automatic Windows startup.

## Display scaling

The popup is as wide as its content needs - the longest application name, the memory value and the header decide it - and it is laid out in logical pixels scaled to the DPI of the monitor it opens on, so it keeps its proportions from 100% to 200% and higher. When Windows sends a DPI-change notification while the popup is open, the geometry and rows are rebuilt for the new scale.

## Note

In Windows WDDM mode, some NVIDIA drivers do not return per-process VRAM usage via `nvidia-smi`. Windows estimates are shown in white and may be inaccurate; total GPU usage still comes from `nvidia-smi`.
