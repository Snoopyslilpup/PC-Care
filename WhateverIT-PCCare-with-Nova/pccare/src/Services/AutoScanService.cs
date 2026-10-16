using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WhateverITPCCare.Models;
using WinForms = System.Windows.Forms;

namespace WhateverITPCCare.Services;

/// <summary>
/// Persisted settings for the automatic background health check.
/// Stored as JSON in %AppData%\WhateverITPCCare\autoscan.json.
/// </summary>
public sealed class AutoScanSettings
{
    /// <summary>Whether the weekly background check is switched on.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>How often the background check runs, in days.</summary>
    public int IntervalDays { get; set; } = 7;
    /// <summary>UTC time the last background check actually ran.</summary>
    public DateTime? LastScanUtc { get; set; }
    /// <summary>Number of warning/critical items the last check found.</summary>
    public int LastIssueCount { get; set; }
    /// <summary>Overall 0-100 score from the last check.</summary>
    public int LastScore { get; set; } = 100;
}

/// <summary>
/// A Pro-only background watchdog. While the user is on Pro and has switched it
/// on, it keeps a system-tray icon alive, runs a full health scan on a weekly
/// schedule, and only pops a Windows notification when something actually needs
/// attention. Silent-until-needed: a clean automatic check says nothing.
///
/// It never changes the system - it just runs the same read-only
/// <see cref="HealthScanner"/> scan the user can run by hand, on a timer.
/// </summary>
public sealed class AutoScanService : IDisposable
{
    private readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhateverITPCCare");
    private string SettingsFile => Path.Combine(_dir, "autoscan.json");

    private readonly Dispatcher _ui;
    private readonly Func<IReadOnlyList<HealthItem>> _scan;
    private readonly Action _openMain;

    private WinForms.NotifyIcon? _tray;
    private Timer? _timer;
    private int _running;              // reentrancy guard for the scan
    private bool _pro;
    private bool _minimizedHintShown;  // only nag "running in the tray" once per session

    public AutoScanSettings Settings { get; private set; } = new();

    /// <summary>Raised (on the UI thread) whenever status or settings change,
    /// so the window can refresh its text.</summary>
    public event Action? Changed;

    /// <summary>Raised (on the UI thread) after every background/health scan with
    /// the full result, so other features (e.g. the desktop companion) can react
    /// to specific findings such as overheating or pending updates.</summary>
    public event Action<IReadOnlyList<HealthItem>>? Reported;

    public AutoScanService(Dispatcher ui, Func<IReadOnlyList<HealthItem>> scan, Action openMain)
    {
        _ui = ui;
        _scan = scan;
        _openMain = openMain;
        Settings = Load();
    }

    // ===== public control surface =====

    /// <summary>Tell the service whether the current plan is Pro. The tray icon
    /// and scheduler only run for a Pro user who has also enabled the feature.</summary>
    public void SetPro(bool pro)
    {
        _pro = pro;
        Reconcile();
    }

    /// <summary>Switch the weekly background check on or off (persisted).</summary>
    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        Save();
        Reconcile();
        RaiseChanged();
    }

    /// <summary>Run a health check right now on a background thread. Used by the
    /// scheduler and by the "Check now" button. A manual (non-auto) run always
    /// reports its result; an automatic run stays silent when all is well.</summary>
    public void RunScanNow(bool auto)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return; // already scanning
        try
        {
            IReadOnlyList<HealthItem> items;
            try { items = _scan(); }
            catch { return; }

            int issues = items.Count(i => i.Status is HealthStatus.Warning or HealthStatus.Critical);
            int score = HealthScanner.Score(items);

            Settings.LastScanUtc = DateTime.UtcNow;
            Settings.LastIssueCount = issues;
            Settings.LastScore = score;
            Save();
            RaiseChanged();

            var snapshot = items;
            _ui.BeginInvoke(new Action(() => Reported?.Invoke(snapshot)));

            if (issues > 0)
            {
                Notify(
                    $"PC health check: {issues} item{(issues == 1 ? "" : "s")} need attention",
                    $"Your check scored {score}/100. Click to see what Whatever IT PC Care found.");
            }
            else if (!auto)
            {
                Notify("PC health check complete", $"All clear - your PC scored {score}/100.");
            }
            // auto + clean  =>  stay silent on purpose
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>One-time-per-session balloon reminding the user the app is still
    /// watching from the tray after they minimise it.</summary>
    public void ShowMinimizedHint()
    {
        if (_minimizedHintShown || !_pro || !Settings.Enabled) return;
        _minimizedHintShown = true;
        Notify("Still watching",
            "Whatever IT PC Care is running quietly in the tray and will alert you if your PC needs attention.");
    }

    /// <summary>Human-readable status line for the Diagnostics card.</summary>
    public string StatusLine()
    {
        if (Settings.LastScanUtc is DateTime last)
        {
            DateTime next = last.AddDays(Settings.IntervalDays);
            string lastStr = last.ToLocalTime().ToString("d MMM yyyy, HH:mm");
            string nextStr = next.ToLocalTime().ToString("d MMM yyyy");
            string result = Settings.LastIssueCount > 0
                ? $"found {Settings.LastIssueCount} item{(Settings.LastIssueCount == 1 ? "" : "s")} needing attention (score {Settings.LastScore}/100)"
                : $"all clear (score {Settings.LastScore}/100)";
            return $"Last background check: {lastStr} - {result}. Next check due around {nextStr}.";
        }
        return "No background check has run yet. The first one runs within a week of switching this on " +
               "(or use \u201cCheck now\u201d to try it immediately).";
    }

    // ===== scheduling / tray lifecycle =====

    private void Reconcile()
    {
        bool active = _pro && Settings.Enabled;
        if (active)
        {
            _ui.BeginInvoke(new Action(EnsureTray));
            // Poll every 30 min and compare against the stored last-run time; this
            // copes better with sleep/hibernate than one long week-length timer.
            _timer ??= new Timer(OnTick, null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(30));
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
            _ui.BeginInvoke(new Action(DisposeTray));
        }
    }

    private void OnTick(object? state)
    {
        if (!_pro || !Settings.Enabled) return;
        DateTime? last = Settings.LastScanUtc;
        if (last.HasValue && (DateTime.UtcNow - last.Value) < TimeSpan.FromDays(Settings.IntervalDays))
            return; // not due yet
        RunScanNow(auto: true);
    }

    private void EnsureTray()
    {
        if (_tray != null) return;
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Whatever IT PC Care"
        };
        _tray.DoubleClick += (_, _) => _ui.BeginInvoke(_openMain);
        _tray.BalloonTipClicked += (_, _) => _ui.BeginInvoke(_openMain);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Whatever IT PC Care", null, (_, _) => _ui.BeginInvoke(_openMain));
        menu.Items.Add("Run a health check now", null, (_, _) => Task.Run(() => RunScanNow(auto: false)));
        _tray.ContextMenuStrip = menu;
    }

    private void DisposeTray()
    {
        if (_tray == null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }

    private void Notify(string title, string body)
    {
        _ui.BeginInvoke(new Action(() =>
        {
            EnsureTray();
            _tray!.BalloonTipTitle = title;
            _tray.BalloonTipText = body;
            _tray.ShowBalloonTip(10000);
        }));
    }

    private static Icon LoadIcon()
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                Icon? ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { /* fall through to a stock icon */ }
        return SystemIcons.Application;
    }

    // ===== persistence =====

    private AutoScanSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AutoScanSettings>(File.ReadAllText(SettingsFile)) ?? new();
        }
        catch { /* corrupt/old file -> defaults */ }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private void RaiseChanged() => _ui.BeginInvoke(new Action(() => Changed?.Invoke()));

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        DisposeTray();
    }
}
