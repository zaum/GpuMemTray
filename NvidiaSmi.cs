using System.Diagnostics;
using Microsoft.Win32;

namespace GpuMemTray;

internal static class NvidiaSmi
{
    // Set when the Windows session is ending (logoff or shutdown). Starting a
    // new child process at that point fails DLL initialization (0xc0000142)
    // and pops an error dialog that also stalls the shutdown, so every spawn
    // site checks this flag first.
    public static volatile bool SessionEnding;

    public static GpuSnapshot Read()
    {
        if (SessionEnding) return GpuSnapshot.Empty;
        try
        {
            var memoryLines = Run("--query-gpu=memory.used,memory.total --format=csv,noheader,nounits");
            var memory = memoryLines.Select(ParseMemory).Where(x => x is not null).Cast<(int used, int total)>().ToList();
            if (memory.Count == 0)
            {
                // nvidia-smi not available, fall back to the Windows counter.
                return FallbackSnapshot();
            }
            var processes = Run("--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader,nounits")
                .Select(ParseProcess).Where(x => x is not null).Cast<GpuProcess>()
                .OrderByDescending(x => x.MemoryMiB ?? -1).ThenBy(x => x.Name).ToList();
            // NVIDIA's WDDM driver often returns [N/A] for process VRAM. Windows keeps
            // the same data in its GPU Process Memory performance counter.
            if (processes.Count == 0 || processes.All(p => p.MemoryMiB is null))
                processes = ReadWindowsProcessMemory();
            return new GpuSnapshot(memory.Sum(x => x.used), memory.Sum(x => x.total), processes, true);
        }
        catch
        {
            // If nvidia-smi fails completely, try the Windows counter as fallback.
            return FallbackSnapshot();
        }
    }

    private static GpuSnapshot FallbackSnapshot()
    {
        var processes = ReadWindowsProcessMemory();
        if (processes.Count == 0) return GpuSnapshot.Empty;
        var totalUsed = processes.Sum(p => p.MemoryMiB ?? 0);
        // The Windows counter only knows the per-process usage, so the total
        // VRAM is read from the registry. When it cannot be determined it
        // stays 0 and Percent reports 0 instead of a fake 100%.
        return new GpuSnapshot(totalUsed, TotalVRAMMiB(), processes, true);
    }

    private static IEnumerable<string> Run(string arguments)
    {
        return RunCapture("nvidia-smi", arguments, 3000)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Starts a child process, drains both output pipes concurrently (so a full
    // buffer can never deadlock the child) and caps the wait with a timeout.
    private static string RunCapture(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        // Never spawn children while the session is tearing down: the child
        // cannot initialize and Windows shows a 0xc0000142 error dialog.
        if (SessionEnding) return string.Empty;
        if (!process.Start()) return string.Empty;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(); } catch { /* the child already exited. */ }
            return string.Empty;
        }
        _ = stderrTask; // drained only to keep the pipe open; its content is unused
        return stdoutTask.GetAwaiter().GetResult();
    }

    private static (int used, int total)? ParseMemory(string line)
    {
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && int.TryParse(parts[0], out var used) && int.TryParse(parts[1], out var total) ? (used, total) : null;
    }

    private static GpuProcess? ParseProcess(string line)
    {
        var parts = line.Split(',', 3, StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        var executable = parts[1].Replace('\\', '/').Split('/').LastOrDefault() ?? parts[1];
        var memText = parts.Length > 2 ? parts[2].Replace("[", "").Replace("]", "").Trim() : "";
        return new GpuProcess(executable, int.TryParse(memText, out var mib) ? mib : null, int.TryParse(parts[0], out var pid) ? pid : null);
    }

    private static List<GpuProcess> ReadWindowsProcessMemory()
    {
        const string script = "(Get-Counter '\\GPU Process Memory(*)\\Dedicated Usage').CounterSamples | Where-Object {$_.CookedValue -gt 0} | ForEach-Object { '{0}|{1}' -f $_.InstanceName,[math]::Round($_.CookedValue / 1MB) }";
        try
        {
            return RunCapture("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", 4000)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('|', 2))
                .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
                .Select(parts => new { Pid = ParsePid(parts[0]), Memory = int.Parse(parts[1]) })
                .Where(x => x.Pid is not null)
                .GroupBy(x => x.Pid!.Value)
                .Select(group => new GpuProcess(ProcessName(group.Key), group.Sum(x => x.Memory), group.Key))
                .OrderByDescending(x => x.MemoryMiB).ToList();
        }
        catch { return []; }
    }

    // Reads the dedicated VRAM size of the NVIDIA display adapters from the
    // registry (HardwareInformation.qwMemorySize). Returns MiB, or 0 when the
    // value cannot be determined.
    private static int TotalVRAMMiB()
    {
        try
        {
            // Display adapter device class; each numeric subkey is one GPU.
            const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var classKey = Registry.LocalMachine.OpenSubKey(displayClass);
            if (classKey is null) return 0;
            long totalBytes = 0;
            foreach (var name in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(name, out _)) continue;
                using var adapterKey = classKey.OpenSubKey(name);
                if (adapterKey?.GetValue("DriverDesc") as string is not { } description
                    || !description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                var raw = adapterKey.GetValue("HardwareInformation.qwMemorySize");
                long bytes = raw switch
                {
                    long qword => qword,
                    int dword => dword,
                    byte[] binary when binary.Length == sizeof(long) => BitConverter.ToInt64(binary, 0),
                    _ => 0
                };
                totalBytes += bytes;
            }
            return (int)Math.Min(totalBytes / (1024 * 1024), int.MaxValue);
        }
        catch { /* Unexpected registry layout; treat the total as unknown. */ }
        return 0;
    }

    private static int? ParsePid(string instance)
    {
        var match = System.Text.RegularExpressions.Regex.Match(instance, @"pid_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var pid) ? pid : null;
    }

    private static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName + ".exe"; }
        catch { return $"PID {pid}"; }
    }
}

internal sealed record GpuProcess(string Name, int? MemoryMiB, int? Pid);

internal sealed record GpuSnapshot(int UsedMiB, int TotalMiB, IReadOnlyList<GpuProcess> Processes, bool Available)
{
    public static readonly GpuSnapshot Empty = new(0, 0, [], false);
    public int Percent => TotalMiB == 0 ? 0 : Math.Clamp((int)Math.Round(UsedMiB * 100d / TotalMiB), 0, 100);
    public string Tooltip => Available ? $"GPU memory: {UsedMiB / 1024d:0.#} / {TotalMiB / 1024d:0.#} GB ({Percent}%)" : "GPU memory: nvidia-smi unavailable";
}
