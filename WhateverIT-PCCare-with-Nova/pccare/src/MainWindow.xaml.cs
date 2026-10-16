using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WhateverITPCCare.Helpers;
using WhateverITPCCare.Models;
using WhateverITPCCare.Services;

namespace WhateverITPCCare;

public partial class MainWindow : Window
{
    // >>> EDIT THESE for your business <<<
    private const string WebsiteUrl = "https://www.whateverit.tech/";
    private const string ContactPhone = "0100250564";              // shown + dialled
    private const string ContactPhoneDisplay = "010 025 0564";

    // SimplyBlu monthly payment links (one per plan).
    private const string BasicPayUrl = "https://pay.simplyblu.co.za/summary/TTWhAQaKNHCdtg_6yoAKSQ";
    private const string ProPayUrl   = "https://pay.simplyblu.co.za/summary/TjWhAQ5AuH6oJTBST6nlsw";
    private const string BasicPrice  = "R65 / month";
    private const string ProPrice    = "R99 / month";

    private readonly SystemInfoService _sys = new();
    private readonly HealthScanner _scanner = new();
    private readonly RepairEngine _repairs = new();
    private readonly LicenseManager _license = new();

    private readonly ObservableCollection<HealthItem> _results = new();
    private Storyboard? _wolfLoop;

    // Pro-only background watchdog (weekly scan + tray notifications).
    private AutoScanService? _autoScan;
    private bool _suppressAutoScanEvent;

    // Pro-only desktop companion wolf ("Nova").
    private CompanionService? _companion;
    private bool _suppressCompanionEvent;
    private bool _suppressVoiceEvent;

    public MainWindow()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _results;
        RepairsList.ItemsSource = _repairs.All;
        LoadHeader();
        _license.Load();
        RefreshLicenseUi();
        VersionLine.Text = "Whatever IT PC Care v1.0  •  © Whatever IT";
        InitRig();
        InitAutoScan();
        InitCompanion();
    }

    // ===== Desktop companion (Pro) =====
    private void InitCompanion()
    {
        _companion = new CompanionService(this);
        _companion.Changed += RefreshCompanionUi;
        // Let Nova react to whatever the background health check finds.
        if (_autoScan != null) _autoScan.Reported += items => _companion!.OnReport(items);
        _companion.SetPro(_license.Current.IsPro);
        RefreshCompanionUi();
    }

    private void RefreshCompanionUi()
    {
        if (_companion == null) return;
        bool pro = _license.Current.IsPro;
        CompanionToggle.IsEnabled = pro;
        CompanionVoiceBox.IsEnabled = pro;
        _suppressCompanionEvent = true;
        CompanionToggle.IsChecked = pro && _companion.IsEnabled;
        _suppressCompanionEvent = false;
        _suppressVoiceEvent = true;
        CompanionVoiceBox.SelectedIndex =
            string.Equals(_companion.Voice, "male", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _suppressVoiceEvent = false;
    }

    private void OnCompanionToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressCompanionEvent || _companion == null) return;
        _companion.SetEnabled(CompanionToggle.IsChecked == true);
        RefreshCompanionUi();
    }

    private void OnCompanionVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVoiceEvent || _companion == null) return;
        _companion.SetVoice(CompanionVoiceBox.SelectedIndex == 1 ? "male" : "female");
    }

    // ===== Automatic background health check (Pro) =====
    private void InitAutoScan()
    {
        _autoScan = new AutoScanService(Dispatcher, () => _scanner.Scan(), BringToForeground);
        _autoScan.Changed += RefreshAutoScanUi;
        _autoScan.SetPro(_license.Current.IsPro);
        RefreshAutoScanUi();

        // Minimise-to-tray so a Pro user who tucks the window away keeps the
        // background watchdog (and tray icon) alive instead of closing it.
        StateChanged += OnWindowStateChanged;
    }

    private void RefreshAutoScanUi()
    {
        if (_autoScan == null) return;
        bool pro = _license.Current.IsPro;

        AutoScanToggle.IsEnabled = pro;
        AutoScanNowButton.IsEnabled = pro && _autoScan.Settings.Enabled;

        _suppressAutoScanEvent = true;
        AutoScanToggle.IsChecked = pro && _autoScan.Settings.Enabled;
        _suppressAutoScanEvent = false;

        AutoScanStatus.Text = pro
            ? _autoScan.StatusLine()
            : "Automatic weekly background health checks are a Pro feature. Upgrade on the " +
              "Plan \u0026 Billing tab and Whatever IT PC Care will quietly watch your PC, alerting " +
              "you only when something needs attention.";
    }

    private void OnAutoScanToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoScanEvent || _autoScan == null) return;
        _autoScan.SetEnabled(AutoScanToggle.IsChecked == true);
        RefreshAutoScanUi();
    }

    private void OnAutoScanNow(object sender, RoutedEventArgs e)
    {
        if (_autoScan == null) return;
        AutoScanStatus.Text = "Running a background check\u2026";
        Task.Run(() => _autoScan.RunScanNow(auto: false));
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _autoScan != null &&
            _license.Current.IsPro && _autoScan.Settings.Enabled)
        {
            Hide();                       // drop the taskbar button; tray icon stays
            _autoScan.ShowMinimizedHint();
        }
    }

    /// <summary>Bring the window back from the tray / minimised state and show
    /// the Diagnostics tab (called from the tray icon and balloon clicks).</summary>
    private void BringToForeground()
    {
        try
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;   // nudge above other windows
            Focus();
            MainTabs.SelectedIndex = 0;        // Diagnostics
        }
        catch { }
    }

    private bool _companionFarewellDone;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Give Nova a brief, best-effort goodbye (recorded shutdown line) before
        // the app actually closes. One-shot: we cancel this first close, let her
        // speak, then close for real a moment later.
        if (!_companionFarewellDone && _companion is { IsActive: true })
        {
            _companionFarewellDone = true;
            try
            {
                _companion.SayGoodbye();
                e.Cancel = true;
                var t = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(1600) };
                t.Tick += (_, _) => { t.Stop(); Close(); };
                t.Start();
                return;
            }
            catch { /* fall through and close normally */ }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Never leave background apps suspended: thaw everything Game Mode paused.
        try { if (_gameMode.IsActive) _gameMode.Disable(); } catch { }
        _companion?.Dispose();
        _autoScan?.Dispose();
        base.OnClosed(e);
    }

    private void LoadHeader()
    {
        PcLine.Text = $"{_sys.MachineName}  •  signed in as {_sys.UserName}";
        OsLine.Text = _sys.WindowsVersion;
        SpecLine.Text = $"{_sys.Processor}  •  {_sys.TotalRam} RAM";
    }

    // ===== Scanning =====
    private async void OnScan(object sender, RoutedEventArgs e)
    {
        // Free trial model: an unlicensed PC gets ONE scan to test the app. Once
        // that is used up, scanning is locked and we send them to the plan screen.
        if (!_license.Current.IsBasicOrBetter && _license.TrialUsed)
        {
            UpdateScanAvailability();
            GoToSubscription("Your free trial scan has already been used on this PC. " +
                "Subscribe to a plan to keep scanning and to use the one-click fixes.");
            return;
        }

        ScanButton.IsEnabled = false;
        ScanLabel.Text = "Scanning…";
        _results.Clear();
        ScoreText.Text = "…";

        // Let the wolf go to work: eyes pulse, head rocks, scan ring spins.
        _wolfLoop = (Storyboard)FindResource("WolfScanLoop");
        _wolfLoop.Begin(this, true);

        IReadOnlyList<HealthItem> items = Array.Empty<HealthItem>();
        try
        {
            items = await Task.Run(() => _scanner.Scan(msg =>
                Dispatcher.Invoke(() => ScanStatus.Text = msg)));
        }
        catch (Exception ex)
        {
            ScanStatus.Text = "The scan could not finish: " + ex.Message;
        }

        foreach (var i in items) _results.Add(i);
        UpdateScore(items);

        int warn = items.Count(x => x.Status is HealthStatus.Warning or HealthStatus.Critical);
        ScanStatus.Text = warn == 0
            ? $"All clear – checked {items.Count} things and found nothing that needs attention."
            : $"Checked {items.Count} things – {warn} need{(warn == 1 ? "s" : "")} your attention (highlighted below).";

        // Stop the working animation and give a quick "howl" to say it's done.
        _wolfLoop?.Stop(this);
        ((Storyboard)FindResource("WolfHowl")).Begin(this);

        if (_license.Current.IsBasicOrBetter)
        {
            ScanButton.IsEnabled = true;
            ScanLabel.Text = "Scan again";
        }
        else
        {
            // That was the single free trial scan - lock scanning until they subscribe.
            _license.MarkTrialUsed();
            ScanStatus.Text += "  •  This was your free trial scan. Subscribe on the Plan \u0026 Billing tab " +
                "to scan again and to unlock the one-click fixes.";
            UpdateScanAvailability();
        }
    }

    private void UpdateScore(IReadOnlyList<HealthItem> items)
    {
        int score = HealthScanner.Score(items);
        ScoreText.Text = items.Count == 0 ? "—" : score.ToString();
        var brush = score >= 85 ? "#FF34C759" : score >= 60 ? "#FFFFB020" : "#FFFF4D4F";
        ScoreRing.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(brush));
    }

    // ===== Repairs =====
    private void OnFixThis(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id && !string.IsNullOrEmpty(id))
            RunRepairById(id);
    }

    private void OnRunRepair(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
            RunRepairById(id);
    }

    private async void RunRepairById(string id)
    {
        var action = _repairs.Find(id);
        if (action == null) return;

        // New trial model: the one-click fixes are a paid feature. An unlicensed
        // user who clicks "Fix this" / "Run" is taken straight to the plan screen
        // instead of running anything.
        if (!_license.Current.IsBasicOrBetter)
        {
            GoToSubscription("The one-click fixes are part of a subscription. " +
                "Choose a plan below to unlock them.");
            return;
        }

        if (action.ProOnly && !_license.Current.IsPro)
        {
            var ask = MessageBox.Show(
                $"“{action.Title}” is an advanced Pro repair ({ProPrice}).\n\nGet Pro now?",
                "Pro feature", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ask == MessageBoxResult.Yes) StartCheckout("PRO", "Pro", ProPrice, ProPayUrl);
            return;
        }

        if (action.RequiresBasic && !_license.Current.IsBasicOrBetter)
        {
            var ask = MessageBox.Show(
                $"“{action.Title}” needs a Basic plan ({BasicPrice}) to run for you.\n\nGet Basic now?",
                "Basic feature", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ask == MessageBoxResult.Yes) StartCheckout("BASIC", "Basic", BasicPrice, BasicPayUrl);
            return;
        }

        var confirm = MessageBox.Show(
            $"{action.Description}\n\nDo you want to run this now?" +
            (action.RequiresAdmin ? "\n\nWindows will ask for Administrator permission." : ""),
            action.Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        string result;
        try
        {
            result = await Task.Run(() => action.Run(msg => { /* progress hook */ }));
        }
        catch (Exception ex)
        {
            result = "That repair could not finish: " + ex.Message;
        }
        MessageBox.Show(result, action.Title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===== Plan & billing =====
    private void OnGetBasic(object sender, RoutedEventArgs e) => StartCheckout("BASIC", "Basic", BasicPrice, BasicPayUrl);
    private void OnGetPro(object sender, RoutedEventArgs e)   => StartCheckout("PRO", "Pro", ProPrice, ProPayUrl);

    private void StartCheckout(string tier, string planName, string price, string url)
    {
        var win = new CheckoutWindow(url, planName, price) { Owner = this };
        bool? ok = win.ShowDialog();
        if (ok != true || !win.PaymentConfirmed) return;

        var info = _license.PurchaseActivate(tier);
        RefreshLicenseUi();
        MessageBox.Show(
            info.Activated
                ? $"Thank you! Your {info.Tier} plan is now active until {info.Expiry:d MMM yyyy}.\n\n" +
                  "It renews when you pay again next month. If a payment is missed, the plan simply " +
                  "pauses until you renew."
                : "We could not activate the plan automatically. Please contact support.",
            "Payment", MessageBoxButton.OK,
            info.Activated ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        var info = _license.Activate(NameBox.Text, KeyBox.Text);
        RefreshLicenseUi();
        MessageBox.Show(
            info.Activated ? $"Thank you! Your {info.Tier} plan is now active." : info.Message,
            "Licence", MessageBoxButton.OK,
            info.Activated ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnDeactivate(object sender, RoutedEventArgs e)
    {
        _license.Deactivate();
        KeyBox.Clear();
        RefreshLicenseUi();
    }

    private void RefreshLicenseUi()
    {
        var c = _license.Current;
        NameBox.Text = c.LicensedTo;
        string plan = c.Activated ? c.Tier : "Free";
        string until = c.Expiry.HasValue && c.Activated ? $" (until {c.Expiry:d MMM yyyy})" : "";
        LicenseStatus.Text = $"Current plan: {plan}{until}\n{c.Message}\n\n" +
            (c.IsPro
                ? "Pro features (advanced repairs) are unlocked."
                : c.IsBasicOrBetter
                    ? "Basic one-click repairs are unlocked. Advanced repairs need Pro."
                    : "You are on the free diagnostics tier. Subscribe to unlock repairs.");

        bool basicActive = c.IsBasicOrBetter;
        BasicActionButton.Content = basicActive ? "Renew Basic" : "Get Basic";
        ProActionButton.Content = c.IsPro ? "Renew Pro" : "Get Pro";

        UpdateScanAvailability();
        ApplyRigAccess();   // flip The Rig lock on/off to match the current plan

        // Keep the background watchdog in step with the plan (starts/stops the
        // tray + scheduler the instant the user upgrades to / drops from Pro).
        _autoScan?.SetPro(_license.Current.IsPro);
        RefreshAutoScanUi();

        // Nova the companion is also Pro-only.
        _companion?.SetPro(_license.Current.IsPro);
        RefreshCompanionUi();
    }

    /// <summary>
    /// Enables or greys out the Scan button based on the plan + whether the one
    /// free trial scan has been used. Licensed users scan without limit; everyone
    /// else gets exactly one scan to test, after which the button greys out.
    /// </summary>
    private void UpdateScanAvailability()
    {
        bool licensed = _license.Current.IsBasicOrBetter;
        bool canScan = licensed || !_license.TrialUsed;

        ScanButton.IsEnabled = canScan;
        ScanButton.Opacity = canScan ? 1.0 : 0.5;   // visibly greyed when locked

        if (canScan)
        {
            ScanLabel.Text = _results.Count > 0 ? "Scan again" : "Scan PC";
            ScanButton.ToolTip = licensed ? null : "You have one free trial scan to test the app.";
        }
        else
        {
            ScanLabel.Text = "Subscribe to scan";
            ScanButton.ToolTip = "Your free trial scan has been used. Subscribe to keep scanning.";
        }
    }

    /// <summary>Switch to the Plan & Billing tab and explain why.</summary>
    private void GoToSubscription(string? reason = null)
    {
        try { MainTabs.SelectedItem = PlanTab; } catch { }
        MessageBox.Show(
            reason ?? "Subscribe to a plan to keep using Whatever IT PC Care \u2013 unlimited scans and " +
            "the one-click fixes.",
            "Subscribe to continue", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===== Links =====
    private void OnWebsite(object sender, RoutedEventArgs e) => ProcessHelper.OpenShell(WebsiteUrl);
    private void OnContact(object sender, RoutedEventArgs e)
    {
        // Start a phone call to Whatever IT (uses the PC's default tel handler;
        // if none is set we fall back to showing the number to dial).
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo($"tel:{ContactPhone}") { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show($"Call Whatever IT on {ContactPhoneDisplay}.",
                "Call Whatever IT", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
