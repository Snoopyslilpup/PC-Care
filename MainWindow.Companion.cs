using System;
using System.Windows;
using WhateverITPCCare.Services;

namespace WhateverITPCCare;

/// <summary>
/// Wires the walking / talking wolf desktop companion into the main window:
/// a single shared instance, the "Wake the wolf" / control-panel buttons on
/// The Rig hero card, and the links back from the companion (open its panel,
/// jump to the diagnostics scan). The companion runs independently of the
/// window once launched, so it keeps roaming even when this window is minimised.
/// </summary>
public partial class MainWindow
{
    private CompanionWindow? _companion;
    private CompanionPanel? _companionPanel;

    /// <summary>Called from the constructor. The wolf no longer starts on its
    /// own: he only appears when the user clicks "Wake the wolf". Here we just
    /// make sure he is dismissed cleanly when the app closes.</summary>
    private void InitCompanion()
    {
        // Make sure the wolf is dismissed cleanly when the app closes.
        Closing += (_, _) =>
        {
            try
            {
                if (_companion != null)
                {
                    _companion.SayFarewell();
                    _companion.Close();
                    _companion = null;
                }
                _companionPanel?.Close();
            }
            catch { }
        };

        // When the main window is minimised (or restored), keep the companion
        // visible and independent on the desktop. It must NOT disappear with
        // the main app.
        StateChanged += (_, _) =>
        {
            try
            {
                if (_companion == null) return;
                if (WindowState == WindowState.Minimized)
                {
                    // Ensure companion stays out and on top of the desktop
                    if (!_companion.IsVisible) _companion.Show();
                    _companion.Visibility = Visibility.Visible;
                    if (_companion.WindowState == WindowState.Minimized)
                        _companion.WindowState = WindowState.Normal;
                    bool top = true;
                    try { top = CompanionConfig.Load().AlwaysOnTop; } catch { }
                    _companion.Topmost = false;
                    _companion.Topmost = top;
                }
            }
            catch { }
        };

        // NOTE: deliberately NOT auto-launching here. The companion must only
        // appear on an explicit "Wake the wolf" click, never on startup.
    }

    /// <summary>Create the single companion instance (or reuse the existing one)
    /// and make sure it is visible.</summary>
    private CompanionWindow LaunchCompanion()
    {
        if (_companion == null)
        {
            _companion = new CompanionWindow();
            // Intentionally NOT setting Owner = this. An owned window is hidden
            // when its owner is minimized; the companion must stay on the desktop.
            _companion.OpenPanelRequested += (_, _) => OpenCompanionPanel();
            _companion.OpenDiagnosticsRequested += (_, _) => ShowDiagnostics();
            _companion.Closed += (_, _) => _companion = null;
            _companion.Show();
        }
        else if (!_companion.IsVisible)
        {
            _companion.Show();
        }
        // Re-assert visibility / topmost in case the main window was just minimised
        try
        {
            _companion.Visibility = Visibility.Visible;
            _companion.Show();
            if (_companion.WindowState == WindowState.Minimized)
                _companion.WindowState = WindowState.Normal;
            bool top = true;
            try { top = CompanionConfig.Load().AlwaysOnTop; } catch { }
            _companion.Topmost = false;
            _companion.Topmost = top;
        }
        catch { }
        return _companion;
    }

    /// <summary>Hero-card button / wolf click: send the wolf out onto the desktop.
    /// This is the ONLY way the companion starts. We persist Enabled=true so a
    /// panel reload won't immediately hide him, then force him visible at a
    /// known on-screen spot.</summary>
    private void OnWakeWolf(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = CompanionConfig.Load();
            if (!cfg.Enabled) { cfg.Enabled = true; cfg.Save(); }
        }
        catch { }

        var wolf = LaunchCompanion();
        try
        {
            wolf.ReloadConfig();   // pick up Enabled=true so it is not hidden
            wolf.WakeOnScreen();   // show + park somewhere visible
            wolf.Activate();
        }
        catch { }
    }

    /// <summary>Hero-card button / companion menu: open the wolf's control panel.</summary>
    private void OnOpenCompanionPanel(object sender, RoutedEventArgs e) => OpenCompanionPanel();

    private void OpenCompanionPanel()
    {
        // Make sure there is a wolf to configure, then show a single panel.
        var wolf = LaunchCompanion();

        if (_companionPanel != null)
        {
            try { _companionPanel.Activate(); } catch { }
            return;
        }

        _companionPanel = new CompanionPanel(CompanionConfig.Load(), wolf) { Owner = this };
        _companionPanel.Closed += (_, _) => _companionPanel = null;
        _companionPanel.ShowDialog();
    }

    /// <summary>The wolf asked to open the PC Care diagnostics: bring the main
    /// window forward and switch to the Diagnostics tab.</summary>
    private void ShowDiagnostics()
    {
        try
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true; Topmost = false;   // nudge to the front
            MainTabs.SelectedIndex = 0;        // Diagnostics is the first tab
        }
        catch { }
    }
}
