using System.Diagnostics;

namespace GpuMemTray;

internal static class NvidiaSmi
{
    // Set when the Windows session is ending (logoff or shutdown). Starting a
    // new child process at that point fails DLL initialization (0xc0000142)
    // and pops an error dialog that also stalls the shutdown, so every spawn
    // site checks this flag first.
    public static volatile bool SessionEnding;
    private static DateTime windowsCounterCacheUntil = DateTime.MinValue;
    private static DateTime windowsCounterBackoffUntil = DateTime.MinValue;
    private static TimeSpan windowsCounterBackoff = TimeSpan.Zero;
    private static List<GpuProcess> windowsCounterCache = [];

    public static GpuSnapshot Read()
    {
        if (SessionEnding) return GpuSnapshot.Empty;
        try
        {
            var memoryLines = Run("--query-gpu=memory.used,memory.total --format=csv,noheader,nounits");
            var memory = memoryLines.Select(ParseMemory).Where(x => x is not null).Cast<(int used, int total)>().ToList();
            if (memory.Count == 0) return GpuSnapshot.Empty;
            var processes = Run("--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader,nounits")
                .Select(ParseProcess).Where(x => x is not null).Cast<GpuProcess>()
                .ToList();
            // WDDM does not expose per-process VRAM through nvidia-smi. Use
            // Windows' counter only as a visibly marked estimate in that case;
            // it is known to over-report on some systems and must never affect
            // the authoritative GPU-wide total above.
            if (processes.Any(p => p.MemoryMiB is null))
            {
                var windowsByPid = ReadWindowsProcessMemory()
                    .Where(p => p.Pid is not null)
                    .GroupBy(p => p.Pid!.Value)
                    .ToDictionary(g => g.Key, g => g.First());
                processes = processes.Select(p =>
                {
                    if (p.MemoryMiB is not null || p.Pid is not int pid || !windowsByPid.TryGetValue(pid, out var estimate)) return p;
                    return p with
                    {
                        MemoryMiB = estimate.MemoryMiB,
                        IsWindowsEstimate = true,
                        ExceedsGpuMemory = estimate.MemoryMiB is int value && value > memory.Sum(x => x.total)
                    };
                }).ToList();
            }
            processes = processes.OrderByDescending(x => x.MemoryMiB ?? -1).ThenBy(x => x.Name).ToList();
            return new GpuSnapshot(memory.Sum(x => x.used), memory.Sum(x => x.total), processes, true);
        }
        catch
        {
            return GpuSnapshot.Empty;
        }
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
        var now = DateTime.UtcNow;
        if (now < windowsCounterCacheUntil || now < windowsCounterBackoffUntil) return windowsCounterCache;

        const string script = "(Get-Counter '\\GPU Process Memory(*)\\Dedicated Usage').CounterSamples | Where-Object {$_.CookedValue -gt 0} | ForEach-Object { '{0}|{1}' -f $_.InstanceName,[math]::Round($_.CookedValue / 1MB) }";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var processes = RunCapture("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", 5000)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('|', 2))
                .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
                .Select(parts => new { Pid = ParsePid(parts[0]), Memory = int.Parse(parts[1]) })
                .Where(x => x.Pid is not null)
                .GroupBy(x => x.Pid!.Value)
                .Select(group => new GpuProcess(ProcessName(group.Key), group.Sum(x => x.Memory), group.Key, true))
                .ToList();

            if (stopwatch.Elapsed >= TimeSpan.FromMilliseconds(4500))
            {
                windowsCounterBackoff = windowsCounterBackoff == TimeSpan.Zero
                    ? TimeSpan.FromSeconds(15)
                    : windowsCounterBackoff < TimeSpan.FromMinutes(5)
                        ? windowsCounterBackoff * 2
                        : windowsCounterBackoff;
                windowsCounterBackoffUntil = DateTime.UtcNow + windowsCounterBackoff;
                return windowsCounterCache;
            }

            windowsCounterBackoff = TimeSpan.Zero;
            windowsCounterBackoffUntil = DateTime.MinValue;
            windowsCounterCache = processes;
            windowsCounterCacheUntil = DateTime.UtcNow.AddSeconds(3);
            return windowsCounterCache;
        }
        catch
        {
            windowsCounterBackoff = windowsCounterBackoff == TimeSpan.Zero
                ? TimeSpan.FromSeconds(15)
                : windowsCounterBackoff < TimeSpan.FromMinutes(5)
                    ? windowsCounterBackoff * 2
                    : windowsCounterBackoff;
            windowsCounterBackoffUntil = DateTime.UtcNow + windowsCounterBackoff;
            return windowsCounterCache;
        }
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

internal sealed record GpuProcess(string Name, int? MemoryMiB, int? Pid, bool IsWindowsEstimate = false, bool ExceedsGpuMemory = false);

internal sealed record GpuSnapshot(int UsedMiB, int TotalMiB, IReadOnlyList<GpuProcess> Processes, bool Available)
{
    public static readonly GpuSnapshot Empty = new(0, 0, [], false);
    public int Percent => TotalMiB == 0 ? 0 : Math.Clamp((int)Math.Round(UsedMiB * 100d / TotalMiB), 0, 100);
    public string Tooltip => Available ? $"GPU memory: {UsedMiB / 1024d:0.#} / {TotalMiB / 1024d:0.#} GB ({Percent}%)" : "GPU memory: nvidia-smi unavailable";
}
