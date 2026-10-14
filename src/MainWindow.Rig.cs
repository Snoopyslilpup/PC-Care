using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WhateverITPCCare.Helpers;
using WhateverITPCCare.Services;

namespace WhateverITPCCare;

/// <summary>
/// "The Rig" dashboard: live hardware monitoring, Windows power-profile
/// switching, maintenance shortcuts and a Steam game hub. All read-only except
/// the power-plan switch (a standard, reversible Windows setting).
/// </summary>
public partial class MainWindow
{
    private readonly RigMonitor _rig = new();
    private readonly GameLibrary _gameLib = new();
    private readonly ObservableCollection<RigGame> _rigGames = new();
    private DispatcherTimer? _rigTimer;

    // Windows built-in power scheme GUIDs.
    private const string PlanSaver   = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string PlanBalanced= "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PlanHigh    = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PlanUltimate= "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private void InitRig()
    {
        RigGamesList.ItemsSource = _rigGames;
        RigAdminHint.Visibility = IsElevatedRig() ? Visibility.Collapsed : Visibility.Visible;

        UpdateCurrentMode();

        _rigTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _rigTimer.Tick += (_, _) => RigTick();

        // The Rig is a Pro-only dashboard. Only start the live monitoring /
        // game scan when the user is actually on Pro; otherwise show the lock.
        ApplyRigAccess();
    }

    /// <summary>
    /// Shows the live dashboard for Pro users and the "upgrade" lock for
    /// everyone else. Safe to call before InitRig has created the timer and
    /// again whenever the licence changes (e.g. right after an upgrade), so the
    /// gate flips the instant the user becomes Pro.
    /// </summary>
    internal void ApplyRigAccess()
    {
        bool pro = _license.Current.IsPro;

        if (RigLockOverlay != null)
            RigLockOverlay.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        if (RigContent != null)
            RigContent.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;

        if (pro)
        {
            // Start monitoring the first time a Pro user reaches the tab.
            if (_rigTimer != null && !_rigTimer.IsEnabled)
            {
                _rigTimer.Start();
                RigTick();            // paint once immediately
                _ = ScanGamesAsync(); // find Steam games in the background
            }
        }
        else
        {
            // Not Pro: make sure nothing keeps running behind the lock.
            _rigTimer?.Stop();
        }
    }

    // ===== live monitoring =====
    private void RigTick()
    {
        RigSnapshot s;
        try { s = _rig.Read(); }
        catch { return; }

        RigCpuTempText.Text  = s.CpuTempC is double ct ? $"{ct:0}\u00b0" : "\u2014";
        RigCpuClockText.Text = s.CpuClockMhz is double cc ? $"{cc / 1000.0:0.0} GHz" : "\u2014 GHz";
        RigGpuTempText.Text  = s.GpuTempC is double gt ? $"{gt:0}\u00b0C" : "\u2014";
        RigGpuClockText.Text = s.GpuClockMhz is double gc ? $"{gc:0} MHz" : "\u2014";
        RigCpuVoltText.Text  = s.CpuVoltage is double cv ? $"{cv:0.000} V" : "\u2014";
        RigCpuNameText.Text  = string.IsNullOrEmpty(s.CpuName) ? "" : s.CpuName;

        RigFanText.Text = (s.CpuFanRpm, s.GpuFanRpm) switch
        {
            (int cf, int gf) => $"{cf} / {gf}",
            (int cf, null)   => cf.ToString(),
            (null, int gf)   => gf.ToString(),
            _                => "\u2014"
        };

        SetBar(RigCpuLoadBar, RigCpuLoadText, s.CpuLoadPct, "%");
        SetBar(RigGpuLoadBar, RigGpuLoadText, s.GpuLoadPct, "%");

        if (s.RamUsedGB is double ru && s.RamTotalGB is double rt)
        {
            RigRamText.Text = $"{ru:0.0} / {rt:0.0} GB";
            RigRamBar.Value = s.RamPct ?? 0;
        }
        else { RigRamText.Text = "\u2014"; RigRamBar.Value = 0; }

        if (s.StorageUsedGB is double su && s.StorageTotalGB is double st)
        {
            RigStorageText.Text = $"{su:0} / {st:0} GB";
            RigStorageBar.Value = s.StoragePct ?? 0;
        }
        else { RigStorageText.Text = "\u2014"; RigStorageBar.Value = 0; }

        // Colour the CPU dial by temperature.
        if (s.CpuTempC is double t)
        {
            string key = t >= 90 ? "BadBrush" : t >= 80 ? "WarnBrush" : "GoodBrush";
            RigCpuRing.Stroke = (TryFindResource(key) as Brush) ?? RigCpuRing.Stroke;
        }
        else
        {
            RigCpuRing.Stroke = (TryFindResource("Card2Brush") as Brush) ?? RigCpuRing.Stroke;
        }
    }

    private static void SetBar(ProgressBar bar, TextBlock label, double? pct, string suffix)
    {
        if (pct is double v)
        {
            bar.Value = v;
            label.Text = $"{v:0}{suffix}";
        }
        else
        {
            bar.Value = 0;
            label.Text = "\u2014";
        }
    }

    // ===== operating / fan modes (Windows power plans) =====
    private void OnRigMode(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;

        switch (tag)
        {
            case "silent":
                ApplyPlan(PlanSaver, "Silent", "quiet and power-saving");
                break;
            case "balanced":
                ApplyPlan(PlanBalanced, "Balanced", "a balance of quiet and quick");
                break;
            case "performance":
                ApplyPlan(PlanHigh, "Performance", "full speed, fans work harder");
                break;
            case "turbo":
                ApplyTurbo();
                break;
            case "windows":
                ApplyPlan(PlanBalanced, "Windows", "let Windows manage it");
                break;
            case "manual":
                ProcessHelper.OpenShell("powercfg.cpl");
                RigModeStatus.Text = "Opened Windows Power Options so you can fine-tune the plan yourself.";
                break;
        }
    }

    private void ApplyPlan(string guid, string label, string blurb)
    {
        var (code, _) = ProcessHelper.Run("powercfg", $"/setactive {guid}", elevated: false, timeoutMs: 15000);
        if (code == 0)
        {
            RigModeChip.Text = label;
            RigModeStatus.Text = $"Mode set to {label} \u2013 {blurb}.";
        }
        else
        {
            RigModeStatus.Text = $"Could not switch mode (Windows returned code {code}).";
        }
    }

    private void ApplyTurbo()
    {
        // Ultimate Performance is hidden on many PCs; create it on first use.
        var (code, _) = ProcessHelper.Run("powercfg", $"/setactive {PlanUltimate}", elevated: false, timeoutMs: 15000);
        if (code != 0)
        {
            ProcessHelper.Run("powercfg", $"-duplicatescheme {PlanUltimate}", elevated: false, timeoutMs: 15000);
            (code, _) = ProcessHelper.Run("powercfg", $"/setactive {PlanUltimate}", elevated: false, timeoutMs: 15000);
        }

        if (code == 0)
        {
            RigModeChip.Text = "Turbo";
            RigModeStatus.Text = "Mode set to Turbo \u2013 Ultimate Performance, maximum speed.";
        }
        else
        {
            ApplyPlan(PlanHigh, "Performance", "full speed (Turbo/Ultimate isn't available on this PC)");
        }
    }

    private void UpdateCurrentMode()
    {
        try
        {
            var (_, output) = ProcessHelper.Run("powercfg", "/getactivescheme", elevated: false, timeoutMs: 15000);
            string o = (output ?? "").ToLowerInvariant();
            string label =
                o.Contains(PlanSaver)    ? "Silent" :
                o.Contains(PlanHigh)     ? "Performance" :
                o.Contains(PlanUltimate) ? "Turbo" :
                o.Contains(PlanBalanced) ? "Windows" :
                "Windows";
            RigModeChip.Text = label;
        }
        catch { }
    }

    // ===== drivers & BIOS / maintenance =====
    private void OnRigWindowsUpdate(object sender, RoutedEventArgs e)
        => ProcessHelper.OpenShell("ms-settings:windowsupdate");

    private void OnRigDeviceManager(object sender, RoutedEventArgs e)
        => ProcessHelper.OpenShell("devmgmt.msc");

    private void OnRigAsusSupport(object sender, RoutedEventArgs e)
        => ProcessHelper.OpenShell("https://www.asus.com/support/download-center/");

    // ===== game library =====
    private async void OnScanGames(object sender, RoutedEventArgs e) => await ScanGamesAsync();

    private async Task ScanGamesAsync()
    {
        RigGamesStatus.Text = "Looking for installed games\u2026";
        try
        {
            var found = await Task.Run(() => _gameLib.Scan());
            _rigGames.Clear();
            foreach (var g in found) _rigGames.Add(g);

            RigGamesStatus.Text = found.Count == 0
                ? "No Steam games found. Install Steam (or games through it) and scan again."
                : $"Found {found.Count} game{(found.Count == 1 ? "" : "s")} \u2013 click Play to launch.";
        }
        catch
        {
            RigGamesStatus.Text = "Could not read the game library on this PC.";
        }
    }

    private void OnLaunchGame(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string uri && !string.IsNullOrEmpty(uri))
            ProcessHelper.OpenShell(uri);
    }

    // ===== helpers =====
    private static bool IsElevatedRig()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
