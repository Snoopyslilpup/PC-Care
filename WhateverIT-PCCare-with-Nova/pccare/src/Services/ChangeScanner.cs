using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace WhateverITPCCare.Services;

/// <summary>A point-in-time, read-only picture of what loads and is installed on
/// the PC: startup programs, installed applications and Windows services.</summary>
public sealed class SystemSnapshot
{
    public DateTime TakenUtc { get; set; }
    public List<string> Startup { get; set; } = new();
    public List<string> Apps { get; set; } = new();
    public List<string> Services { get; set; } = new();
}

/// <summary>Result of comparing the current snapshot against the previous one.</summary>
public sealed class SnapshotDiff
{
    public DateTime? PreviousUtc { get; set; }
    public bool IsFirstRun { get; set; }
    public List<string> StartupAdded { get; set; } = new();
    public List<string> StartupRemoved { get; set; } = new();
    public List<string> AppsAdded { get; set; } = new();
    public List<string> AppsRemoved { get; set; } = new();
    public List<string> ServicesAdded { get; set; } = new();
    public List<string> ServicesRemoved { get; set; } = new();

    public int TotalChanges =>
        StartupAdded.Count + StartupRemoved.Count + AppsAdded.Count +
        AppsRemoved.Count + ServicesAdded.Count + ServicesRemoved.Count;
}

/// <summary>
/// "What changed?" scanner. Snapshots startup programs, installed apps and
/// services, saves a baseline in %AppData%, and on the next run highlights what
/// is NEW or GONE since last time - great for catching silently-added startup
/// items, bundled bloatware, or unexpected services. Entirely read-only.
/// </summary>
public sealed class ChangeScanner
{
    private readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhateverITPCCare");
    private string SnapshotFile => Path.Combine(_dir, "snapshot.json");

    /// <summary>Take a fresh snapshot, diff it against the saved baseline, then
    /// save the fresh one as the new baseline. First ever run just saves.</summary>
    public SnapshotDiff CheckAndUpdate()
    {
        SystemSnapshot current = Capture();
        SystemSnapshot? previous = LoadPrevious();
        var diff = new SnapshotDiff();

        if (previous == null)
        {
            diff.IsFirstRun = true;
        }
        else
        {
            diff.PreviousUtc = previous.TakenUtc;
            diff.StartupAdded = Added(previous.Startup, current.Startup);
            diff.StartupRemoved = Added(current.Startup, previous.Startup);
            diff.AppsAdded = Added(previous.Apps, current.Apps);
            diff.AppsRemoved = Added(current.Apps, previous.Apps);
            diff.ServicesAdded = Added(previous.Services, current.Services);
            diff.ServicesRemoved = Added(current.Services, previous.Services);
        }

        Save(current);
        return diff;
    }

    private static List<string> Added(List<string> oldList, List<string> newList)
        => newList.Where(x => !oldList.Contains(x, StringComparer.OrdinalIgnoreCase))
                  .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    // ===== capture =====
    private static SystemSnapshot Capture() => new()
    {
        TakenUtc = DateTime.UtcNow,
        Startup = CaptureStartup(),
        Apps = CaptureApps(),
        Services = CaptureServices(),
    };

    private static List<string> CaptureStartup()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRunValues(set, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
        AddRunValues(set, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run");
        AddRunValues(set, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run");

        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        })
        {
            try
            {
                if (Directory.Exists(folder))
                    foreach (var f in Directory.EnumerateFiles(folder))
                        set.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRunValues(HashSet<string> set, RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
                if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
        }
        catch { }
    }

    private static List<string> CaptureApps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddUninstall(set, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        AddUninstall(set, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        AddUninstall(set, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddUninstall(HashSet<string> set, RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            if (k == null) return;
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using var e = k.OpenSubKey(sub);
                    if (e == null) continue;
                    string? name = e.GetValue("DisplayName") as string;
                    object? sysComp = e.GetValue("SystemComponent");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (sysComp is int sc && sc == 1) continue; // hide OS plumbing
                    set.Add(name.Trim());
                }
                catch { }
            }
        }
        catch { }
    }

    private static List<string> CaptureServices()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (k != null)
            {
                foreach (var sub in k.GetSubKeyNames())
                {
                    try
                    {
                        using var e = k.OpenSubKey(sub);
                        string? display = e?.GetValue("DisplayName") as string;
                        set.Add(string.IsNullOrWhiteSpace(display) ? sub : display.Trim());
                    }
                    catch { set.Add(sub); }
                }
            }
        }
        catch { }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ===== persistence =====
    private SystemSnapshot? LoadPrevious()
    {
        try
        {
            if (File.Exists(SnapshotFile))
                return JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(SnapshotFile));
        }
        catch { }
        return null;
    }

    private void Save(SystemSnapshot snap)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(SnapshotFile,
                JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }
}
