using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using WhateverITPCCare.Services;

namespace WhateverITPCCare;

/// <summary>
/// Tier-2 "differentiator" features bolted onto The Rig (Pro) and Diagnostics:
///  - live line graphs of CPU/GPU temperature and CPU load
///  - a short, safe CPU stress test with a thermal verdict
///  - one-click Game Mode (pause background apps + boost power plan)
///  - read-only driver awareness
///  - a "What changed?" diff scanner (on the Diagnostics tab)
/// </summary>
public partial class MainWindow
{
    // ===== live graphs =====
    private const int GraphMax = 120;                 // ~2.5 min of history at 1.2s
    private readonly List<double> _gCpuTemp = new();
    private readonly List<double> _gGpuTemp = new();
    private readonly List<double> _gCpuLoad = new();

    private void UpdateGraphs(RigSnapshot s)
    {
        Push(_gCpuTemp, s.CpuTempC ?? 0);
        Push(_gGpuTemp, s.GpuTempC ?? 0);
        Push(_gCpuLoad, s.CpuLoadPct ?? 0);

        if (RigGraphCanvas == null) return;
        double w = RigGraphCanvas.ActualWidth, h = RigGraphCanvas.ActualHeight;
        if (w < 2 || h < 2) return;

        Draw(RigCpuTempLine, _gCpuTemp, w, h);
        Draw(RigGpuTempLine, _gGpuTemp, w, h);
        Draw(RigCpuLoadLine, _gCpuLoad, w, h);
    }

    private static void Push(List<double> buf, double v)
    {
        buf.Add(v);
        if (buf.Count > GraphMax) buf.RemoveAt(0);
    }

    // All three series share a 0-100 scale (°C and % line up nicely).
    private static void Draw(Polyline line, List<double> buf, double w, double h)
    {
        if (line == null) return;
        var pts = new PointCollection();
        int n = buf.Count;
        if (n >= 2)
        {
            double step = w / (GraphMax - 1);
            // right-align the newest sample at the right edge
            double startX = w - (n - 1) * step;
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(0, Math.Min(100, buf[i]));
                double x = startX + i * step;
                double y = h - (v / 100.0) * h;
                pts.Add(new Point(x, y));
            }
        }
        line.Points = pts;
    }

    // ===== safe CPU stress test =====
    private CancellationTokenSource? _stressCts;
    private bool _stressActive;
    private double _peakCpuTemp, _peakGpuTemp, _peakCpuClock;
    private const int StressSeconds = 30;
    private const double StressAbortTempC = 95;

    /// <summary>Called from RigTick while a stress test is running so we capture
    /// peak temperatures/clocks from the live sensor readings.</summary>
    private void TrackStressPeaks(RigSnapshot s)
    {
        if (s.CpuTempC is double ct)
        {
            if (ct > _peakCpuTemp) _peakCpuTemp = ct;
            if (ct >= StressAbortTempC) _stressCts?.Cancel();   // safety stop
        }
        if (s.GpuTempC is double gt && gt > _peakGpuTemp) _peakGpuTemp = gt;
        if (s.CpuClockMhz is double cc && cc > _peakCpuClock) _peakCpuClock = cc;
    }

    private async void OnRunStress(object sender, RoutedEventArgs e)
    {
        if (_stressActive) return;
        _stressActive = true;
        _peakCpuTemp = _peakGpuTemp = _peakCpuClock = 0;
        bool aborted = false;

        RunStressButton.IsEnabled = false;
        StopStressButton.IsEnabled = true;
        RigStressVerdict.Text = "";
        RigStressStatus.Text = $"Warming up all CPU cores for up to {StressSeconds} seconds\u2026 " +
                               "watch the temperature graph climb. You can stop any time.";

        _stressCts = new CancellationTokenSource();
        _stressCts.CancelAfter(TimeSpan.FromSeconds(StressSeconds));
        var token = _stressCts.Token;

        try
        {
            await Task.Run(() =>
            {
                int cores = Math.Max(1, Environment.ProcessorCount);
                var workers = new Task[cores];
                for (int i = 0; i < cores; i++)
                    workers[i] = Task.Run(() =>
                    {
                        double x = 1.0001;
                        while (!token.IsCancellationRequested)
                        {
                            // pure arithmetic busy-work - no I/O, nothing persisted
                            x = Math.Sqrt(x * 1.0000001 + 1) + Math.Sin(x) * Math.Cos(x);
                            if (x > 1e9) x = 1.0001;
                        }
                    });
                Task.WaitAll(workers);
            });
        }
        catch { /* workers exit cleanly on cancel */ }

        aborted = _peakCpuTemp >= StressAbortTempC;
        FinishStress(aborted);
    }

    private void OnStopStress(object sender, RoutedEventArgs e) => _stressCts?.Cancel();

    private void FinishStress(bool aborted)
    {
        _stressActive = false;
        _stressCts?.Dispose();
        _stressCts = null;
        RunStressButton.IsEnabled = true;
        StopStressButton.IsEnabled = false;

        string peakStr = _peakCpuTemp > 0 ? $"{_peakCpuTemp:0}\u00b0C" : "not reported";
        string clockStr = _peakCpuClock > 0 ? $"{_peakCpuClock / 1000.0:0.0} GHz" : "—";
        RigStressStatus.Text = $"Test finished. Peak CPU temperature: {peakStr}  \u2022  peak clock: {clockStr}.";

        if (_peakCpuTemp <= 0)
        {
            RigStressVerdict.Text = "Verdict: the test ran, but no live CPU temperature was available " +
                "to judge cooling. Run Whatever IT PC Care as administrator to read temperatures.";
            SetVerdictColour("MutedBrush");
            return;
        }

        string verdict; string colour;
        if (aborted)
        {
            verdict = $"Verdict: the CPU hit {_peakCpuTemp:0}\u00b0C and the test stopped early for safety. " +
                "That is very hot - clean the fans/heatsink, check airflow, and consider fresh thermal paste. " +
                "If it keeps happening, give Whatever IT a call.";
            colour = "BadBrush";
        }
        else if (_peakCpuTemp < 70)
        {
            verdict = $"Verdict: excellent - the CPU peaked at just {_peakCpuTemp:0}\u00b0C under full load. " +
                "Cooling is in great shape.";
            colour = "GoodBrush";
        }
        else if (_peakCpuTemp < 85)
        {
            verdict = $"Verdict: healthy - the CPU peaked at {_peakCpuTemp:0}\u00b0C under full load, " +
                "comfortably within a normal range.";
            colour = "GoodBrush";
        }
        else if (_peakCpuTemp < 95)
        {
            verdict = $"Verdict: warm - the CPU reached {_peakCpuTemp:0}\u00b0C. It is coping, but a dust " +
                "clean-out and better airflow would give it more headroom.";
            colour = "WarnBrush";
        }
        else
        {
            verdict = $"Verdict: very hot - the CPU reached {_peakCpuTemp:0}\u00b0C. Clean the cooling and " +
                "check airflow; consider a technician if it persists.";
            colour = "BadBrush";
        }
        RigStressVerdict.Text = verdict;
        SetVerdictColour(colour);
    }

    private void SetVerdictColour(string key)
    {
        if (TryFindResource(key) is Brush b) RigStressVerdict.Foreground = b;
    }

    // ===== Game Mode =====
    private readonly GameModeService _gameMode = new();

    private async void OnGameModeToggle(object sender, RoutedEventArgs e)
    {
        GameModeButton.IsEnabled = false;
        string result;
        if (!_gameMode.IsActive)
        {
            RigGameModeStatus.Text = "Turning Game Mode on\u2026";
            result = await Task.Run(() => _gameMode.Enable());
        }
        else
        {
            RigGameModeStatus.Text = "Turning Game Mode off\u2026";
            result = await Task.Run(() => _gameMode.Disable());
        }
        RigGameModeStatus.Text = result;
        UpdateGameModeButton();
        GameModeButton.IsEnabled = true;
    }

    private void UpdateGameModeButton()
    {
        GameModeButton.Content = _gameMode.IsActive ? "⏹  Turn Game Mode off" : "🎮  Turn Game Mode on";
    }

    // ===== driver awareness =====
    private readonly DriverScanner _driverScanner = new();
    private readonly ObservableCollection<DriverInfo> _drivers = new();

    private void InitRigPlus()
    {
        RigDriversList.ItemsSource = _drivers;
        UpdateGameModeButton();
    }

    private async void OnScanDrivers(object sender, RoutedEventArgs e)
    {
        RigDriversStatus.Text = "Reading your current driver versions\u2026";
        try
        {
            var found = await Task.Run(() => _driverScanner.Scan());
            _drivers.Clear();
            foreach (var d in found) _drivers.Add(d);
            int stale = found.Count(d => d.Stale);
            RigDriversStatus.Text = found.Count == 0
                ? "Could not read driver details on this PC."
                : stale == 0
                    ? $"Checked {found.Count} key drivers - all look reasonably current."
                    : $"Checked {found.Count} key drivers - {stale} " +
                      $"{(stale == 1 ? "is" : "are")} old enough to be worth checking for an update.";
        }
        catch
        {
            RigDriversStatus.Text = "Could not read driver details on this PC.";
        }
    }

    // ===== "What changed?" diff scanner (Diagnostics tab) =====
    private readonly ChangeScanner _changes = new();
    private readonly ObservableCollection<string> _changeLines = new();

    private void InitChanges() => ChangeList.ItemsSource = _changeLines;

    private async void OnCheckChanges(object sender, RoutedEventArgs e)
    {
        CheckChangesButton.IsEnabled = false;
        ChangeStatus.Text = "Taking a snapshot of startup items, installed apps and services\u2026";
        SnapshotDiff diff;
        try { diff = await Task.Run(() => _changes.CheckAndUpdate()); }
        catch
        {
            ChangeStatus.Text = "Could not read the system snapshot on this PC.";
            CheckChangesButton.IsEnabled = true;
            return;
        }

        _changeLines.Clear();
        if (diff.IsFirstRun)
        {
            ChangeStatus.Text = "Baseline saved. Run this again in a day or two and it will show exactly " +
                "what has been added or removed since now - handy for spotting bloatware or unwanted startup items.";
        }
        else if (diff.TotalChanges == 0)
        {
            ChangeStatus.Text = $"No changes since {diff.PreviousUtc?.ToLocalTime():d MMM yyyy, HH:mm}. " +
                "Nothing new has been installed, added to startup, or registered as a service.";
        }
        else
        {
            ChangeStatus.Text = $"{diff.TotalChanges} change" +
                $"{(diff.TotalChanges == 1 ? "" : "s")} since {diff.PreviousUtc?.ToLocalTime():d MMM yyyy, HH:mm}:";
            AddSection("▲ New startup items", diff.StartupAdded);
            AddSection("▼ Removed startup items", diff.StartupRemoved);
            AddSection("▲ Newly installed apps", diff.AppsAdded);
            AddSection("▼ Removed apps", diff.AppsRemoved);
            AddSection("▲ New services", diff.ServicesAdded);
            AddSection("▼ Removed services", diff.ServicesRemoved);
        }
        CheckChangesButton.IsEnabled = true;
    }

    private void AddSection(string header, List<string> items)
    {
        if (items.Count == 0) return;
        _changeLines.Add(header + $"  ({items.Count})");
        foreach (var it in items.Take(40)) _changeLines.Add("    • " + it);
        if (items.Count > 40) _changeLines.Add($"    …and {items.Count - 40} more");
    }
}
