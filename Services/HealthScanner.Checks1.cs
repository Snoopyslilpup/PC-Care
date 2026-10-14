using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

public partial class HealthScanner
{
    private IEnumerable<HealthItem> CheckDiskSpace()
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            double freePct = d.TotalSize == 0 ? 100 : 100.0 * d.AvailableFreeSpace / d.TotalSize;
            var status = freePct < 7 ? HealthStatus.Critical
                        : freePct < 15 ? HealthStatus.Warning
                        : HealthStatus.Good;
            yield return new HealthItem
            {
                Category = "Storage",
                Title = $"Free space on drive {d.Name.TrimEnd('\\')}",
                Status = status,
                Detail = $"{Fmt(d.AvailableFreeSpace)} free of {Fmt(d.TotalSize)} ({freePct:0}% free)",
                Explanation = status == HealthStatus.Good
                    ? "This drive has plenty of room."
                    : "Windows slows down and updates can fail when a drive gets too full. " +
                      "Disk Cleanup can remove old Windows Update files and other large " +
                      "leftovers to free real space – far more than clearing temp alone.",
                RepairId = status == HealthStatus.Good ? null : "disk-cleanup"
            };
        }
    }

    private IEnumerable<HealthItem> CheckDriveHealth()
    {
        var results = new List<HealthItem>();
        using var s = new ManagementObjectSearcher("SELECT Model,Status FROM Win32_DiskDrive");
        foreach (ManagementObject mo in s.Get())
        {
            string model = mo["Model"]?.ToString()?.Trim() ?? "Disk";
            string st = mo["Status"]?.ToString()?.Trim() ?? "Unknown";
            bool ok = string.Equals(st, "OK", StringComparison.OrdinalIgnoreCase);
            results.Add(new HealthItem
            {
                Category = "Storage",
                Title = $"Drive health – {model}",
                Status = ok ? HealthStatus.Good : HealthStatus.Warning,
                Detail = $"SMART status: {st}",
                Explanation = ok
                    ? "The drive is reporting itself as healthy."
                    : "The drive is not reporting a healthy status. Back up your important files " +
                      "soon and consider having the drive checked by Whatever IT.",
            });
        }
        if (results.Count == 0)
            results.Add(Info("Storage", "Drive health", "No physical-drive health data available on this PC."));
        return results;
    }

    private IEnumerable<HealthItem> CheckMemory()
    {
        double total = _sys.GetTotalRamBytes();
        double free = _sys.GetFreeRamBytes();
        if (total <= 0) { yield return Info("Memory", "Memory", "Memory information is not available."); yield break; }
        double usedPct = 100.0 * (total - free) / total;
        var status = usedPct > 90 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Memory",
            Title = "Memory (RAM) in use",
            Status = status,
            Detail = $"{usedPct:0}% used – {Fmt(total - free)} of {Fmt(total)}",
            Explanation = status == HealthStatus.Good
                ? "Your PC has memory to spare right now."
                : "Memory is almost full, which can make the PC feel sluggish. Closing programs " +
                  "you are not using, or restarting the PC, usually helps.",
        };
    }

    private IEnumerable<HealthItem> CheckTempFiles()
    {
        long size = 0;
        foreach (var dir in TempDirs())
            size += FolderSize(dir);
        var status = size > 3L * 1024 * 1024 * 1024 ? HealthStatus.Warning : HealthStatus.Good;

        // Only offer a cleanup when there is a worthwhile amount to clear.
        // A running Windows PC always holds ~100–300 MB of temp that programs
        // have open right now; those files can't be deleted and come straight
        // back, so a low threshold just nags "Fix this" forever even after a
        // cleanup. 500 MB keeps the button honest.
        bool offerClean = size > 500L * 1024 * 1024;

        yield return new HealthItem
        {
            Category = "Cleanup",
            Title = "Temporary files",
            Status = status,
            Detail = $"About {Fmt(size)} of temporary files",
            Explanation = offerClean
                ? "Temporary files are safe to delete – Windows and your programs recreate " +
                  "anything they still need. Clearing them frees disk space."
                : "This is a normal amount. Some of it is in use by programs running right " +
                  "now, so Windows keeps those files locked and recreates them – there is " +
                  "nothing to fix here.",
            RepairId = offerClean ? "clean-temp" : null
        };
    }

    private IEnumerable<HealthItem> CheckRecycleBin()
    {
        long size = 0;
        foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            string rb = Path.Combine(d.Name, "$Recycle.Bin");
            try { if (Directory.Exists(rb)) { size += FolderSize(rb); } } catch { }
        }
        var status = size > 2L * 1024 * 1024 * 1024 ? HealthStatus.Warning : HealthStatus.Good;
        yield return new HealthItem
        {
            Category = "Cleanup",
            Title = "Recycle Bin",
            Status = status,
            Detail = size > 0 ? $"About {Fmt(size)} of deleted files" : "Empty",
            Explanation = "Emptying the Recycle Bin permanently removes files you already deleted " +
                          "and frees that space. Only empty it if you are sure you do not need them.",
            RepairId = size > 50L * 1024 * 1024 ? "empty-recycle" : null
        };
    }

    // ---- shared helpers ----
    // On a standard Windows setup Path.GetTempPath() and LocalAppData\Temp are
    // the SAME folder, so we normalise and de-duplicate – otherwise the user's
    // temp directory gets counted (and cleaned) twice, inflating the reported
    // size and making the number barely move after a cleanup.
    internal static IEnumerable<string> TempDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var raw in new[]
                 {
                     Path.GetTempPath(),
                     Path.Combine(windows, "Temp"),
                     Path.Combine(local, "Temp"),
                 })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string norm;
            try { norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
            catch { norm = raw; }
            if (seen.Add(norm)) yield return norm;
        }
    }

    internal static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            var di = new DirectoryInfo(path);
            if (!di.Exists) return 0;
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    internal static string Fmt(double bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {u[i]}";
    }

    private static HealthItem Info(string cat, string title, string explain) => new()
    {
        Category = cat, Title = title, Status = HealthStatus.Info, Detail = "—", Explanation = explain
    };
}
