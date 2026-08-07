using System.Diagnostics;

namespace GpuMemTray;

internal static class NvidiaSmi
{
    public static GpuSnapshot Read()
    {
        try
        {
            var memoryLines = Run("--query-gpu=memory.used,memory.total --format=csv,noheader,nounits");
            var memory = memoryLines.Select(ParseMemory).Where(x => x is not null).Cast<(int used, int total)>().ToList();
            var processes = Run("--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader,nounits")
                .Select(ParseProcess).Where(x => x is not null).Cast<GpuProcess>()
                .OrderByDescending(x => x.MemoryMiB ?? -1).ThenBy(x => x.Name).ToList();
            // NVIDIA's WDDM driver often returns [N/A] for process VRAM. Windows keeps
            // the same data in its GPU Process Memory performance counter.
            if (processes.Count == 0 || processes.All(p => p.MemoryMiB is null))
                processes = ReadWindowsProcessMemory();
            if (memory.Count == 0)
            {
                // nvidia-smi not available, fall back to Windows counter
                processes = ReadWindowsProcessMemory();
                if (processes.Count > 0)
                {
                    var totalUsed = processes.Sum(p => p.MemoryMiB ?? 0);
                    return new GpuSnapshot(totalUsed, totalUsed, processes, true);
                }
                return GpuSnapshot.Empty;
            }
            return new GpuSnapshot(memory.Sum(x => x.used), memory.Sum(x => x.total), processes, true);
        }
        catch
        {
            // If nvidia-smi fails completely, try Windows counter as fallback
            var processes = ReadWindowsProcessMemory();
            if (processes.Count > 0)
            {
                var totalUsed = processes.Sum(p => p.MemoryMiB ?? 0);
                return new GpuSnapshot(totalUsed, totalUsed, processes, true);
            }
            return GpuSnapshot.Empty;
        }
    }

    private static IEnumerable<string> Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null) return [];
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        return new GpuProcess(executable, int.TryParse(memText, out var mib) ? mib : null);
    }

    private static List<GpuProcess> ReadWindowsProcessMemory()
    {
        const string script = "(Get-Counter '\\GPU Process Memory(*)\\Dedicated Usage').CounterSamples | Where-Object {$_.CookedValue -gt 0} | ForEach-Object { '{0}|{1}' -f $_.InstanceName,[math]::Round($_.CookedValue / 1MB) }";
        try
        {
            return RunProcess("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
                .Select(line => line.Split('|', 2))
                .Where(parts => parts.Length == 2 && int.TryParse(parts[1], out _))
                .Select(parts => new { Pid = ParsePid(parts[0]), Memory = int.Parse(parts[1]) })
                .Where(x => x.Pid is not null)
                .GroupBy(x => x.Pid!.Value)
                .Select(group => new GpuProcess(ProcessName(group.Key), group.Sum(x => x.Memory)))
                .OrderByDescending(x => x.MemoryMiB).ToList();
        }
        catch { return []; }
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

    private static IEnumerable<string> RunProcess(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        if (process is null) return [];
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(4000);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

internal sealed record GpuProcess(string Name, int? MemoryMiB);

internal sealed record GpuSnapshot(int UsedMiB, int TotalMiB, IReadOnlyList<GpuProcess> Processes, bool Available)
{
    public static readonly GpuSnapshot Empty = new(0, 0, [], false);
    public int Percent => TotalMiB == 0 ? 0 : Math.Clamp((int)Math.Round(UsedMiB * 100d / TotalMiB), 0, 100);
    public string Tooltip => Available ? $"GPU memory: {UsedMiB / 1024d:0.#} / {TotalMiB / 1024d:0.#} GB ({Percent}%)" : "GPU memory: nvidia-smi unavailable";
}
