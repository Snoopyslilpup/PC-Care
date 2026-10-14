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

    /// <summary>Called from the constructor. Starts the wolf automatically when
    /// the saved settings say he should be shown.</summary>
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

        try
        {
            var cfg = CompanionConfig.Load();
            if (cfg.Enabled) LaunchCompanion();
        }
        catch { /* never let the companion stop the main app from opening */ }
    }

    /// <summary>Create the single companion instance (or reuse the existing one)
    /// and make sure it is visible.</summary>
    private CompanionWindow LaunchCompanion()
    {
        if (_companion == null)
        {
            _companion = new CompanionWindow();
            _companion.OpenPanelRequested += (_, _) => OpenCompanionPanel();
            _companion.OpenDiagnosticsRequested += (_, _) => ShowDiagnostics();
            _companion.Closed += (_, _) => _companion = null;
            _companion.Show();
        }
        else if (!_companion.IsVisible)
        {
            _companion.Show();
        }
        return _companion;
    }

    /// <summary>Hero-card button / wolf click: send the wolf out onto the desktop.</summary>
    private void OnWakeWolf(object sender, RoutedEventArgs e)
    {
        var wolf = LaunchCompanion();
        try { wolf.Activate(); } catch { }
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
