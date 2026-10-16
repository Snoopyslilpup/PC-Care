using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WhateverITPCCare.Models;

namespace WhateverITPCCare;

/// <summary>Persisted on/off state for the desktop companion.</summary>
public sealed class CompanionSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Which recorded voice set to use: "female" or "male".</summary>
    public string Voice { get; set; } = "female";
}

/// <summary>
/// Drives "Nova", the desktop companion wolf. Owns the transparent overlay
/// window, walks the wolf around the edges of the screen, hides him whenever a
/// game or any full-screen app is in front (so he never covers gameplay or
/// video), and gives spoken tips/warnings that are wired to the Pro automatic
/// health check (overheating, pending updates, low disk, security, etc.) plus a
/// lightweight live temperature watch.
///
/// Everything he does is cosmetic and read-only - he reports, he never changes
/// the system. Tied to Pro because he piggybacks on the Pro auto-scan.
/// </summary>
public sealed class CompanionService : IDisposable
{
    private readonly Services.RigMonitor _rig = new();

    private CompanionWindow? _win;
    private bool _pro, _starting, _onScreen = true;

    private DispatcherTimer? _mover;   // ~30ms: steps the walk animation
    private DispatcherTimer? _brain;   // a few seconds: decides the next action
    private DispatcherTimer? _watch;   // 1s: full-screen guard + temp poll

    // movement state (screen pixels, within the working area)
    private double _x, _targetX, _speed = 2.6, _winW = 240, _winH = 260;
    private bool _walking, _busy;      // _busy = mid-warning, don't wander
    private int _watchN;

    // resting / boredom / sleep
    private bool _asleep;              // dozing because the PC is idle
    private string _pose = "idle";     // last procedural pose we asked for
    private int _boredTicks;           // BrainTicks spent doing nothing interesting
    private static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(120); // no input -> doze off

    // warning debounce
    private readonly Dictionary<string, DateTime> _lastSaid = new();
    private static readonly TimeSpan SayCooldown = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan TempCooldown = TimeSpan.FromMinutes(5);

    private readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhateverITPCCare");
    private string File_ => Path.Combine(_dir, "companion.json");

    public CompanionSettings Settings { get; private set; } = new();
    public event Action? Changed;

    /// <summary>Set when Nova was switched on but failed to appear; shown in the
    /// settings card so the user sees WHY instead of a silent dead toggle.</summary>
    private string? _lastError;

    public CompanionService(Window owner)
    {
        Settings = Load();
    }

    public bool IsEnabled => Settings.Enabled;
    public string Voice => Settings.Voice;

    public void SetPro(bool pro) { _pro = pro; Reconcile(); }

    public void SetVoice(string gender)
    {
        Settings.Voice = string.Equals(gender, "male", StringComparison.OrdinalIgnoreCase) ? "male" : "female";
        Save();
        _ = _win?.SetVoiceAsync(Settings.Voice);
        Changed?.Invoke();
    }

    public void SetEnabled(bool on)
    {
        Settings.Enabled = on; Save(); Reconcile();
        Changed?.Invoke();
    }

    /// <summary>True while Nova is actually on screen (used for a goodbye line).</summary>
    public bool IsActive => _win != null;

    /// <summary>Best-effort farewell when the app is closing: play the recorded
    /// shutdown line and wave. Fire-and-forget; the caller gives it a moment
    /// before the window closes.</summary>
    public void SayGoodbye()
    {
        if (_win == null) return;
        try
        {
            _asleep = false; _busy = true; _walking = false;
            _ = _win.PlayAsync("talk"); _pose = "talk";
            _ = _win.SpeakAsync("Bye for now - I'll keep watch next time. Take care!",
                                "Nova", "", "shutdown");
        }
        catch { }
    }

    /// <summary>Human-readable status for the settings card.</summary>
    public string StatusLine()
    {
        if (!_pro)
            return "Nova, your desktop companion wolf, is a Pro feature. She roams your screen, " +
                   "stays out of games, and speaks up when your PC needs attention.";
        if (_lastError != null)
            return "Nova couldn't start: " + _lastError;
        return Settings.Enabled
            ? "Nova is on - she will wander your desktop, hide during games/full-screen video, " +
              "and give spoken tips when the health check finds something."
            : "Nova is off. Switch her on and a friendly 3D wolf will live on your desktop and " +
              "warn you about things like overheating or pending updates.";
    }

    // ===== lifecycle =====
    private async void Reconcile()
    {
        bool active = _pro && Settings.Enabled;
        if (active) { if (_win == null && !_starting) await StartAsync(); }
        else Stop();
    }

    private async System.Threading.Tasks.Task StartAsync()
    {
        _starting = true;
        try
        {
            _win = new CompanionWindow { Owner = null };
            _win.Width = _winW; _win.Height = _winH;
            PlaceAtRestStart();
            _win.Show();
            await _win.InitializeAsync();

            // Don't hang forever if the page never becomes ready. Previously a
            // missing .glb / missing assets left this awaiting Ready with no
            // timeout, so Nova silently never appeared and the toggle looked
            // dead. Now we time out, record WHY, and stop cleanly.
            var readyTask = _win.Ready;
            var finished = await System.Threading.Tasks.Task.WhenAny(
                readyTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(20)));
            if (finished != readyTask)
            {
                _lastError = "Nova timed out waiting for her 3D page to load (20s). " +
                             "See companion-log.txt in %AppData%\\WhateverITPCCare.";
                Changed?.Invoke(); Stop(); return;
            }
            if (!readyTask.Result)
            {
                _lastError = _win?.LastError ??
                    "Nova could not load (see companion-log.txt in %AppData%\\WhateverITPCCare).";
                Changed?.Invoke(); Stop(); return;
            }
            _lastError = null;

            await _win.FaceAsync(1);
            await _win.SetVoiceAsync(Settings.Voice);
            await _win.PlayAsync("idle"); _pose = "idle"; _asleep = false;
            await _win.SpeakAsync("Hi, I'm Nova. I'll keep an eye on your PC and let you know if " +
                                  "anything needs attention.", "Nova", "", "startup");

            _mover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _mover.Tick += (_, _) => MoveTick();
            _mover.Start();

            _brain = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _brain.Tick += (_, _) => BrainTick();
            _brain.Start();

            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watch.Tick += (_, _) => WatchTick();
            _watch.Start();
        }
        catch { Stop(); }
        finally { _starting = false; }
    }

    private void Stop()
    {
        _mover?.Stop(); _brain?.Stop(); _watch?.Stop();
        _mover = _brain = _watch = null;
        _asleep = false; _walking = false; _busy = false; _pose = "idle";
        if (_win != null) { try { _win.Close(); } catch { } _win = null; }
    }

    private Rect WorkArea()
    {
        var a = SystemParameters.WorkArea;
        return new Rect(a.Left, a.Top, a.Width, a.Height);
    }

    private void PlaceAtRestStart()
    {
        var wa = WorkArea();
        _x = wa.Right - _winW - 40;
        _targetX = _x;
        if (_win != null)
        {
            _win.Left = _x;
            _win.Top = wa.Bottom - _winH + 18;   // let feet sit near the taskbar
        }
    }
    //__APPEND_C__

    // ===== walking =====
    private async void MoveTick()
    {
        if (_win == null || !_onScreen) return;
        var wa = WorkArea();
        _win.Top = wa.Bottom - _winH + 18;

        if (!_walking) return;
        double dx = _targetX - _x;
        if (Math.Abs(dx) <= _speed)
        {
            _x = _targetX; _win.Left = _x;
            _walking = false;
            if (!_busy) { await _win.PlayAsync("idle"); _pose = "idle"; }
            return;
        }
        int dir = dx > 0 ? 1 : -1;
        _x += dir * _speed;
        _win.Left = _x;
    }

    private async void StartWalkTo(double targetX)
    {
        var wa = WorkArea();
        _targetX = Math.Max(wa.Left + 10, Math.Min(wa.Right - _winW - 10, targetX));
        int dir = (_targetX >= _x) ? 1 : -1;
        _walking = true; _pose = "walk";
        if (_win != null) { await _win.FaceAsync(dir); await _win.PlayAsync("walk"); }
    }

    // ===== behaviour brain =====
    private void BrainTick()
    {
        if (_win == null || !_onScreen || _busy || _walking || _asleep) return;
        var wa = WorkArea();
        int roll = Random.Shared.Next(100);

        // When she has nothing to warn about she keeps herself occupied: wanders,
        // sits down for a rest, plays with a "file", or hops - so she always looks
        // alive rather than frozen.
        if (_pose == "sit" || _pose == "play")
        {
            // Already resting - usually stay a bit longer, sometimes get up.
            if (roll < 55) { _boredTicks++; return; }
            GoPose("idle");
            if (roll < 80) { double t = wa.Left + Random.Shared.NextDouble() * (wa.Width - _winW); StartWalkTo(t); }
            return;
        }

        if (roll < 35)
        {
            double t = wa.Left + Random.Shared.NextDouble() * (wa.Width - _winW);
            StartWalkTo(t);
            _boredTicks = 0;
        }
        else if (roll < 48)
        {
            _ = _win.PlayAsync("jump"); _boredTicks = 0;   // a playful hop
        }
        else
        {
            // Standing idle: if this keeps happening she's "bored" - sit or play.
            if (++_boredTicks >= 2)
            {
                _boredTicks = 0;
                GoPose(roll < 74 ? "sit" : "play");
                if (Random.Shared.Next(100) < 30) _ = _win.VoiceAsync("random");
            }
        }
    }

    private void GoPose(string pose)
    {
        _pose = pose;
        _ = _win?.PlayAsync(pose);
    }

    // ===== 1s guard: hide for games/full-screen, doze when idle, poll temp =====
    private void WatchTick()
    {
        if (_win == null) return;
        bool blocked = IsFullscreenForeground();
        if (blocked && _onScreen)
        {
            _onScreen = false;
            _ = _win.HushAsync();
            _win.Hide();
        }
        else if (!blocked && !_onScreen)
        {
            _onScreen = true;
            _win.Show();
        }
        if (!_onScreen) return;

        // Fade off to sleep when the computer hasn't been touched for a while,
        // and wake up the moment the user comes back.
        double idleSec = SecondsSinceInput();
        if (!_asleep && !_busy && !_walking && idleSec >= SleepAfter.TotalSeconds)
        {
            _asleep = true; _walking = false;
            GoPose("sleep");
        }
        else if (_asleep && idleSec < 4)
        {
            _asleep = false;
            GoPose("idle");
            if (Random.Shared.Next(100) < 45) _ = _win.VoiceAsync("random");  // a little yawn/greeting
        }
        if (_asleep) return;

        if (++_watchN % 15 == 0) PollTemperature();   // ~every 15s
    }

    private void PollTemperature()
    {
        try
        {
            var s = _rig.Read();
            double? c = s.CpuTempC, g = s.GpuTempC;
            double hot = Math.Max(c ?? 0, g ?? 0);
            if (hot >= 90 && DueTemp("temp"))
            {
                string which = (g ?? 0) >= (c ?? 0) ? "graphics card" : "processor";
                Say("temp",
                    $"Heads up - your {which} is running hot at about {hot:0} degrees. " +
                    "Closing a few heavy apps or giving the fans a clean would help.", "warn", "hot");
            }
        }
        catch { }
    }

    // ===== reacting to the auto-scan result =====
    public void OnReport(IReadOnlyList<HealthItem> items)
    {
        if (_win == null || !_onScreen || _asleep || items == null) return;
        bool Bad(HealthItem i) => i.Status is HealthStatus.Warning or HealthStatus.Critical;
        var bad = items.Where(Bad).ToList();
        if (bad.Count == 0)
        {
            // All clear - now and then give a cheerful "all good" line.
            if (DueTemp("good") && Random.Shared.Next(100) < 70)
                Say("good", "Good news - I had a look and everything's running nicely.", "", "good");
            return;
        }

        // Pick the single most important thing to say (priority ordered).
        var line = PickLine(bad);
        if (line != null) Say(line.Value.key, line.Value.text, "warn", line.Value.cat);
    }

    private (string key, string text, string cat)? PickLine(List<HealthItem> bad)
    {
        HealthItem? Find(Func<HealthItem, bool> p) => bad.FirstOrDefault(p);
        bool Has(string cat, string kw) => bad.Any(i =>
            i.Category.Equals(cat, StringComparison.OrdinalIgnoreCase) &&
            i.Title.Contains(kw, StringComparison.OrdinalIgnoreCase));

        if (Has("Hardware", "Temperature") || Has("Hardware", "cooling"))
            return ("temp", "Your health check flagged temperature and cooling. It is worth " +
                            "checking the fans and airflow before it gets worse.", "hot");
        if (bad.Any(i => i.Category.Equals("Security", StringComparison.OrdinalIgnoreCase)))
            return ("security", "Your security needs a look - antivirus or the firewall isn't fully " +
                                "protecting you right now. Open PC Care to see the details.", "");
        if (bad.Any(i => i.Category.Equals("Updates", StringComparison.OrdinalIgnoreCase)))
            return ("updates", "Your PC has updates waiting. A quick restart will get you protected " +
                               "and running smoothly again.", "");
        var disk = Find(i => i.Category.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
                             i.Title.Contains("Free space", StringComparison.OrdinalIgnoreCase));
        if (disk != null)
            return ("disk", "One of your drives is getting full. Clearing some space will keep " +
                            "things nice and quick.", "");
        if (bad.Any(i => i.Category.Equals("Stability", StringComparison.OrdinalIgnoreCase)))
            return ("stability", "Windows logged a few errors recently. Running the repairs in PC Care " +
                                 "should help settle things down.", "");
        int n = bad.Count;
        return ("generic", $"I spotted {n} thing{(n == 1 ? "" : "s")} that could use your attention. " +
                           "Open PC Care whenever you get a moment.", "");
    }

    // ===== speaking =====
    private bool DueTemp(string key)
    {
        if (_lastSaid.TryGetValue(key, out var t) && DateTime.UtcNow - t < TempCooldown) return false;
        return true;
    }

    private async void Say(string key, string text, string mood, string cat = "")
    {
        if (_win == null || !_onScreen) return;
        if (_lastSaid.TryGetValue(key, out var t) && DateTime.UtcNow - t < SayCooldown) return;
        _lastSaid[key] = DateTime.UtcNow;

        _busy = true;
        _walking = false;
        if (_asleep) { _asleep = false; await _win.PlayAsync("idle"); _pose = "idle"; }
        // trot to roughly the centre so the message is easy to see
        var wa = WorkArea();
        double centre = wa.Left + (wa.Width - _winW) / 2;
        int dir = centre >= _x ? 1 : -1;
        await _win.FaceAsync(dir);
        await _win.PlayAsync("walk"); _walking = true; _targetX = centre; _pose = "walk";

        // wait until she arrives (or ~3.5s max), then speak
        for (int i = 0; i < 120 && _walking; i++) await System.Threading.Tasks.Task.Delay(30);
        _walking = false;
        await _win.SpeakAsync(text, "Nova", mood, cat);
        _pose = "talk";
        await System.Threading.Tasks.Task.Delay(300);
        _busy = false;
    }

    // ===== Win32 full-screen detection =====
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    // ===== Win32 user-idle detection (drives the "fall asleep" behaviour) =====
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    private static double SecondsSinceInput()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            uint idleMs = unchecked(GetTickCount() - lii.dwTime);
            return idleMs / 1000.0;
        }
        catch { return 0; }
    }

    private bool IsFullscreenForeground()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
            if (!GetWindowRect(fg, out var r)) return false;

            // Compare against the full virtual screen (monitor the window is on).
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            int w = r.R - r.L, h = r.B - r.T;
            // Covers (at least) the whole primary screen with no gaps => treat as
            // a game / full-screen video and step aside.
            bool covers = r.L <= 0 && r.T <= 0 && w >= sw - 1 && h >= sh - 1;
            return covers;
        }
        catch { return false; }
    }

    // ===== persistence =====
    private CompanionSettings Load()
    {
        try { if (System.IO.File.Exists(File_))
                return JsonSerializer.Deserialize<CompanionSettings>(System.IO.File.ReadAllText(File_)) ?? new(); }
        catch { }
        return new();
    }
    private void Save()
    {
        try { Directory.CreateDirectory(_dir);
              System.IO.File.WriteAllText(File_, JsonSerializer.Serialize(Settings)); }
        catch { }
    }

    public void Dispose() => Stop();
}
