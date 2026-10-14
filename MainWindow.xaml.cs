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
        InitCompanion();
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
