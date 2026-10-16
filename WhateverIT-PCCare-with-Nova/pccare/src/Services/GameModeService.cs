using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using WhateverITPCCare.Helpers;

namespace WhateverITPCCare.Services;

/// <summary>
/// One-click "Game Mode". Temporarily SUSPENDS a curated, conservative list of
/// well-known non-essential background apps (cloud sync, chat, music, etc.) and
/// switches Windows to a high-performance power plan, so a game gets the machine
/// to itself. Everything is fully reversible: turning Game Mode off RESUMES the
/// exact processes it paused and restores the previous power plan.
///
/// SAFE BY DESIGN: it only ever touches processes whose name is on the positive
/// allow-list below. It never suspends system/critical processes, launchers it
/// might need (Steam), the game, or this app - anything not on the list is left
/// completely alone. Suspending (not killing) means no unsaved work is lost;
/// the app simply freezes and then thaws untouched.
/// </summary>
public sealed class GameModeService
{
    // kernel32 / ntdll for reversible process suspend-resume.
    private const int PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")]
    private static extern uint NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")]
    private static extern uint NtResumeProcess(IntPtr handle);

    // Windows built-in "High performance" power scheme.
    private const string PlanHigh = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    /// <summary>Process names (without .exe, lower-case) that are SAFE to pause
    /// before a game. Deliberately conservative - cloud sync, chat, music and
    /// updaters only. No launchers (Steam etc.), no system processes.</summary>
    private static readonly string[] Pausable =
    {
        "onedrive", "dropbox", "googledrivesync", "googledrivefs",
        "spotify", "discord", "slack", "teams", "ms-teams", "msteams",
        "skype", "zoom", "whatsapp", "telegram",
        "acrobat", "acrotray", "creativecloud", "adobeupdateservice",
        "ccleaner", "utorrent", "qbittorrent", "bittorrent",
        "officeclicktorun", "onenote", "outlook",
        "nvcontainer", "razersynapse", "logioptionsplus",
    };

    private readonly List<int> _suspended = new();
    private string? _previousScheme;

    public bool IsActive { get; private set; }
    public IReadOnlyList<string> PausedNames { get; private set; } = Array.Empty<string>();

    /// <summary>Turn Game Mode on: pause the curated apps and boost the power plan.
    /// Returns a short human-readable summary of what it did.</summary>
    public string Enable()
    {
        if (IsActive) return "Game Mode is already on.";

        _suspended.Clear();
        var names = new List<string>();
        int self = Process.GetCurrentProcess().Id;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                string name = p.ProcessName.ToLowerInvariant();
                if (!Pausable.Any(x => name == x || name.StartsWith(x))) continue;

                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
                if (h == IntPtr.Zero) continue;
                uint rc = NtSuspendProcess(h);
                CloseHandle(h);
                if (rc == 0)
                {
                    _suspended.Add(p.Id);
                    if (!names.Contains(p.ProcessName)) names.Add(p.ProcessName);
                }
            }
            catch { /* skip anything we can't touch */ }
        }

        // Boost the power plan (remember the current one so we can put it back).
        _previousScheme = GetActiveScheme();
        ProcessHelper.Run("powercfg", $"/setactive {PlanHigh}", elevated: false, timeoutMs: 15000);

        PausedNames = names;
        IsActive = true;

        return names.Count == 0
            ? "Game Mode on - power plan boosted to Performance. No background apps needed pausing."
            : $"Game Mode on - boosted the power plan and paused {names.Count} background app" +
              $"{(names.Count == 1 ? "" : "s")}: {string.Join(", ", names)}.";
    }

    /// <summary>Turn Game Mode off: resume everything it paused and restore the
    /// previous power plan. Returns a short summary.</summary>
    public string Disable()
    {
        if (!IsActive) return "Game Mode is already off.";

        int resumed = 0;
        foreach (int pid in _suspended)
        {
            try
            {
                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero) continue;   // process has since exited - fine
                if (NtResumeProcess(h) == 0) resumed++;
                CloseHandle(h);
            }
            catch { }
        }
        _suspended.Clear();

        if (!string.IsNullOrEmpty(_previousScheme))
            ProcessHelper.Run("powercfg", $"/setactive {_previousScheme}", elevated: false, timeoutMs: 15000);

        IsActive = false;
        var count = PausedNames.Count;
        PausedNames = Array.Empty<string>();
        return $"Game Mode off - restored the power plan and resumed {resumed} of {count} paused app" +
               $"{(count == 1 ? "" : "s")}.";
    }

    private static string? GetActiveScheme()
    {
        try
        {
            var (_, output) = ProcessHelper.Run("powercfg", "/getactivescheme", elevated: false, timeoutMs: 15000);
            // Output looks like: "Power Scheme GUID: <guid>  (Name)"
            var parts = (output ?? "").Split(':');
            if (parts.Length >= 2)
            {
                string rest = parts[1].Trim();
                int sp = rest.IndexOf(' ');
                return sp > 0 ? rest.Substring(0, sp) : rest;
            }
        }
        catch { }
        return null;
    }
}
