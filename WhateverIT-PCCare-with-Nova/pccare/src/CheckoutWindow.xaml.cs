using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WhateverITPCCare.Helpers;

namespace WhateverITPCCare;

/// <summary>
/// An in-app, embedded payment window. It loads the SimplyBlu payment page in a
/// WebView2 control and watches the page for a “thank you for your payment” /
/// success confirmation. The instant that confirmation appears, the window
/// closes with <see cref="PaymentConfirmed"/> = true and the caller mints a
/// 30-day licence locally – no email, no key to type.
///
/// NOTE: detecting the browser confirmation is convenient but not tamper-proof
/// (a determined user could reach a success page without paying). That risk is
/// deliberately accepted here: every plan is only 30 days, so anything forged
/// self-expires, and the SECRET in LicenseCore can be rotated. Harden later
/// with a server-side webhook check if abuse ever appears.
/// </summary>
public partial class CheckoutWindow : Window
{
    // Phrases that indicate a completed payment. Lower-cased, matched anywhere
    // in the visible page text. Add the exact wording SimplyBlu shows if needed.
    private static readonly string[] SuccessPhrases =
    {
        "thank you for your payment",
        "thank you for your order",
        "payment successful",
        "payment was successful",
        "payment success",
        "successfully paid",
        "transaction successful",
        "transaction approved",
        "payment received",
        "payment complete",
        "your payment is complete",
    };

    private readonly string _url;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _done;

    /// <summary>True once a successful payment has been detected/confirmed.</summary>
    public bool PaymentConfirmed { get; private set; }

    public CheckoutWindow(string url, string planName, string price)
    {
        InitializeComponent();
        _url = url;
        HeadLine.Text = $"Get {planName} – {price}";
        _timer.Tick += async (_, __) => await CheckForSuccess();
        Loaded += OnLoaded;
        Closed += (_, __) => _timer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Web.EnsureCoreWebView2Async();
            Web.CoreWebView2.NavigationCompleted += async (_, __) => await CheckForSuccess();
            Web.Source = new Uri(_url);
            _timer.Start();
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing / failed. Fall back to the default browser.
            MessageBox.Show(
                "The in-app payment window could not start (the Microsoft WebView2 runtime may be " +
                "missing on this PC).\n\nYour payment page will open in your web browser instead. " +
                "After it confirms the payment was successful, come back here and choose " +
                "“I've completed payment”.\n\nTechnical detail: " + ex.Message,
                "Payment", MessageBoxButton.OK, MessageBoxImage.Information);
            try { ProcessHelper.OpenShell(_url); } catch { }
            StatusLine.Text = "Paying in your browser – click “I've completed payment” when done.";
        }
    }

    private async Task CheckForSuccess()
    {
        if (_done || Web.CoreWebView2 == null) return;
        try
        {
            string raw = await Web.CoreWebView2.ExecuteScriptAsync(
                "(document && document.body) ? document.body.innerText : ''");
            string text = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
            string hay = text.ToLowerInvariant();
            foreach (var p in SuccessPhrases)
                if (hay.Contains(p)) { Succeed(); return; }
        }
        catch { /* transient script/navigation error – ignore and retry on next tick */ }
    }

    private void Succeed()
    {
        if (_done) return;
        _done = true;
        PaymentConfirmed = true;
        _timer.Stop();
        StatusLine.Text = "Payment confirmed – activating…";
        try { DialogResult = true; } catch { }
        Close();
    }

    private void OnManualPaid(object sender, RoutedEventArgs e)
    {
        var ask = MessageBox.Show(
            "Only confirm this once your payment has actually gone through and the page shows a " +
            "successful payment or receipt.\n\nActivate your plan now?",
            "Confirm payment", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Yes) Succeed();
    }
}
