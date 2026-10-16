using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using WhateverITPCCare.Helpers;
using WhateverITPCCare.Models;

namespace WhateverITPCCare.Services;

/// <summary>
/// The catalogue of SAFE repairs. Every action is either a well-known Windows
/// maintenance command or a conservative file-cleanup that Windows can recover
/// from. Nothing here touches personal documents, the registry’s risky areas,
/// or “magic optimisation” tweaks. Anything beyond this is deliberately left to
/// a Whatever IT technician.
/// </summary>
public class RepairEngine
{
    [DllImport("shell32.dll")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public IReadOnlyList<RepairAction> All => _actions;

    public RepairAction? Find(string id)
    {
        foreach (var a in _actions) if (a.Id == id) return a;
        return null;
    }

    private readonly List<RepairAction> _actions = new()
    {
        new RepairAction
        {
            Id = "clean-temp",
            RequiresBasic = true,
            Title = "Clear temporary files",
            Description = "Deletes leftover temporary files from Windows and your programs to free " +
                          "disk space. Safe – anything still needed is recreated automatically.",
            Run = log =>
            {
                long freed = 0; int files = 0;
                foreach (var dir in HealthScanner.TempDirs())
                {
                    log($"Cleaning {dir}");
                    (long f, int c) = CleanFolder(dir);
                    freed += f; files += c;
                }
                return $"Removed {files} files, freed about {HealthScanner.Fmt(freed)}.";
            }
        },
        new RepairAction
        {
            Id = "empty-recycle",
            RequiresBasic = true,
            Title = "Empty the Recycle Bin",
            Description = "Permanently removes files already in the Recycle Bin to free space. " +
                          "Only do this if you are sure you no longer need those files.",
            Run = log =>
            {
                log("Emptying Recycle Bin…");
                // 0x7 = no confirmation + no progress + no sound
                int rc = SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);
                return rc == 0 ? "Recycle Bin emptied." : "Recycle Bin was already empty or could not be emptied.";
            }
        },
        new RepairAction
        {
            Id = "flush-dns",
            RequiresBasic = true,
            Title = "Refresh network (flush DNS)",
            Description = "Clears the DNS cache. Fixes many ‘a website won’t load but others do’ " +
                          "problems without touching your network settings.",
            Run = log =>
            {
                log("Flushing DNS cache…");
                var (_, outp) = ProcessHelper.Run("ipconfig", "/flushdns");
                return outp.Contains("Successfully") ? "DNS cache cleared." : "DNS cache flush completed.";
            }
        },
        new RepairAction
        {
            Id = "disk-cleanup",
            Title = "Open Windows Disk Cleanup",
            Description = "Launches the built-in Windows Disk Cleanup tool so you can tick extra " +
                          "categories (like old update files) and clean them.",
            // GUI tools must be launched fire-and-forget via the shell. Using the
            // console Run() here redirected their output and BLOCKED until the
            // window closed, so the tool often never appeared to open – which is
            // why the Safe Repairs looked like they did nothing.
            Run = log => { log("Opening Disk Cleanup…"); ProcessHelper.OpenShell("cleanmgr.exe"); return "Disk Cleanup is opening…"; }
        },
        new RepairAction
        {
            Id = "startup-manager",
            Title = "Manage startup programs",
            Description = "Opens the Windows Startup Apps settings where you can safely turn off " +
                          "programs you don’t need launching with Windows.",
            Run = log => { log("Opening Startup Apps settings…"); ProcessHelper.OpenShell("ms-settings:startupapps"); return "Startup Apps settings is opening…"; }
        },
        new RepairAction
        {
            Id = "open-security",
            Title = "Open Windows Security",
            Description = "Opens Windows Security so you can turn on antivirus, the firewall, or run " +
                          "a Microsoft Defender scan.",
            Run = log => { log("Opening Windows Security…"); ProcessHelper.OpenShell("windowsdefender:"); return "Windows Security opened."; }
        },
        new RepairAction
        {
            Id = "battery-report",
            Title = "Create a battery health report",
            Description = "Generates Windows’ own battery report (design vs. current capacity) and " +
                          "saves it to your Documents folder.",
            Run = log =>
            {
                string outp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "battery-report.html");
                log("Generating battery report…");
                ProcessHelper.Run("powercfg", $"/batteryreport /output \"{outp}\"");
                return File.Exists(outp) ? $"Saved to {outp}" : "Battery report command finished.";
            }
        },
        // ---- Advanced (Pro) repairs ----
        new RepairAction
        {
            Id = "sfc",
            Title = "Repair Windows system files (SFC)",
            Description = "Runs System File Checker to find and repair corrupted Windows files. " +
                          "Needs Administrator approval and can take several minutes.",
            RequiresAdmin = true, ProOnly = true,
            Run = log =>
            {
                log("Running SFC /scannow (approve the Administrator prompt)…");
                var (code, _) = ProcessHelper.Run("cmd.exe", "/c sfc /scannow & pause", elevated: true);
                return code == 0 ? "System File Checker finished." : "System File Checker ran (see its window for details).";
            }
        },
        new RepairAction
        {
            Id = "dism",
            Title = "Repair the Windows image (DISM)",
            Description = "Runs DISM RestoreHealth to repair the underlying Windows image that SFC " +
                          "relies on. Needs Administrator approval and an internet connection.",
            RequiresAdmin = true, ProOnly = true,
            Run = log =>
            {
                log("Running DISM /RestoreHealth (approve the Administrator prompt)…");
                ProcessHelper.Run("cmd.exe", "/c DISM /Online /Cleanup-Image /RestoreHealth & pause", elevated: true);
                return "DISM finished (see its window for details).";
            }
        },
        new RepairAction
        {
            Id = "reset-wu",
            Title = "Fix stuck Windows Update",
            Description = "Stops Windows Update, clears its download cache, and restarts it – the " +
                          "standard fix for updates that are stuck or failing. Needs Administrator approval.",
            RequiresAdmin = true, ProOnly = true,
            Run = log =>
            {
                log("Resetting Windows Update components (approve the Administrator prompt)…");
                string cmd = "net stop wuauserv & net stop bits & " +
                             "ren %systemroot%\\SoftwareDistribution SoftwareDistribution.old & " +
                             "net start bits & net start wuauserv & echo Done & pause";
                ProcessHelper.Run("cmd.exe", "/c " + cmd, elevated: true);
                return "Windows Update was reset. Check for updates again.";
            }
        },
    };

    private static (long freed, int files) CleanFolder(string path)
    {
        long freed = 0; int files = 0;
        var di = new DirectoryInfo(path);
        if (!di.Exists) return (0, 0);

        // Walk the tree ourselves so a single locked / access-denied sub-folder
        // can't abort the whole sweep (which is what left big chunks behind and
        // made the number barely move after a "clean"). We never delete the
        // temp root itself – only its contents.
        var stack = new Stack<DirectoryInfo>();
        stack.Push(di);
        var visited = new List<DirectoryInfo>();

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            visited.Add(cur);

            FileInfo[] filesHere = Array.Empty<FileInfo>();
            try { filesHere = cur.GetFiles(); } catch { }
            foreach (var f in filesHere)
            {
                try
                {
                    long len = f.Length;
                    f.Attributes = FileAttributes.Normal; // clear read-only
                    f.Delete();
                    freed += len; files++;
                }
                catch { /* in use or protected – skip */ }
            }

            try { foreach (var sub in cur.GetDirectories()) stack.Push(sub); } catch { }
        }

        // Remove now-empty sub-directories deepest-first; skip the root.
        for (int i = visited.Count - 1; i >= 0; i--)
        {
            if (visited[i].FullName.Equals(di.FullName, StringComparison.OrdinalIgnoreCase)) continue;
            try { visited[i].Delete(false); } catch { /* not empty / in use – skip */ }
        }

        return (freed, files);
    }
}
