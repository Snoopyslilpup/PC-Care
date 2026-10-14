using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhateverITPCCare.Services;

namespace WhateverITPCCare;

/// <summary>
/// The walking, talking wolf desktop companion. A borderless, transparent,
/// always-on-top window that roams the desktop, reacts to clicks, narrates with
/// a cheeky personality and - the serious part - watches CPU / GPU temperatures
/// and steps in (honestly) when the PC runs hot.
/// </summary>
public partial class CompanionWindow : Window
{
    private readonly RigMonitor _rig = new();
    private readonly FanControl _fans = new();
    private readonly WolfVoice _voice = new();
    private readonly WolfAudio _audio = new();
    private CompanionConfig _cfg;

    private DispatcherTimer? _brain;    // decides what to do next
    private DispatcherTimer? _mover;    // walks toward the current target
    private DispatcherTimer? _monitor;  // reads temperatures

    private enum Behaviour { Idle, Walk, Sit, Sleep, Scratch, Look, Yawn, Stretch, Play }
    private Behaviour _behaviour = Behaviour.Idle;
    private WolfMood _mood = WolfMood.Happy;
    private enum Thermal { Normal, Warning, Critical }
    private Thermal _thermal = Thermal.Normal;

    private double _targetX;
    private double _targetY;
    private bool _hasTargetY;
    private int _facing = 1;             // 1 = facing right, -1 = facing left
    private double _bobPhase;
    private double _scale = 1.0;         // base companion size multiplier
    private bool _simpleMode;            // true when cutout parts failed to load
                                         // and we fall back to one full sprite
    private double _legSwing;            // eased 0..1 walk-cycle limb amplitude
    private bool _busy;                  // true while a timed pose plays out
    private DateTime _nextDecision = DateTime.MinValue;
    private DateTime _lastChit = DateTime.MinValue;
    private DateTime _lastLongSession = DateTime.MinValue;
    private readonly DateTime _sessionStart = DateTime.Now;
    private bool _gaming;
    private bool _coolingEngaged;
    private readonly Random _rng = new();

    private bool _dragging;
    private bool _dragMoved;
    private Point _dragMouseStart;
    private double _dragLeftStart, _dragTopStart;

    /// <summary>Raised when the user asks (double-click / menu) to open the
    /// companion control panel. The owner (MainWindow) handles it.</summary>
    public event EventHandler? OpenPanelRequested;
    /// <summary>Raised when the user asks to open the main PC Care diagnostics.</summary>
    public event EventHandler? OpenDiagnosticsRequested;

    public CompanionWindow()
    {
        InitializeComponent();
        _cfg = CompanionConfig.Load();
        ApplyConfig();
        Loaded += OnLoaded;
        Closing += (_, _) => Teardown();
    }

    // ----- window styling: tool window, no Alt-Tab, no activation steal -----
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int newLong);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
        catch { }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Park the wolf standing near the bottom-left of the work area.
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 60;
        Top = wa.Bottom - Height;

        // Seed per-hardware temperature thresholds from the detected CPU / GPU.
        try
        {
            var snap = _rig.Read();
            ThermalProfile.SeedDefaults(_cfg, snap.CpuName, snap.GpuName);
        }
        catch { }

        _brain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _brain.Tick += (_, _) => BrainTick();
        _mover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _mover.Tick += (_, _) => MoveTick();
        _monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitor.Tick += (_, _) => MonitorTick();

        _brain.Start();
        _mover.Start();
        _monitor.Start();

        SetMood(WolfMood.Happy);
        Say(WolfLines.Pick(WolfLines.Startup), WolfMood.Happy, 5, WolfClip.Startup);
    }

    /// <summary>Apply the current config to the live window (called at start and
    /// whenever the control panel saves changes).</summary>
    public void ApplyConfig()
    {
        Topmost = _cfg.AlwaysOnTop;
        _voice.Style = _cfg.Voice;
        _voice.CompanionFemale = _cfg.Companion == CompanionGender.Female;
        _audio.SetGender(_cfg.Companion);
        ApplyCharacterImage();

        _scale = Math.Max(0.6, Math.Min(1.8, _cfg.Scale));
        UpdateFacing();
        Width = Math.Max(240, 150 * _scale);
        Height = Math.Max(300, 300 * _scale + 40);

        if (!_cfg.Enabled) Hide();
    }

    /// <summary>Apply the base size + facing to the shared sprite transform.</summary>
    private void UpdateFacing()
    {
        Facing.ScaleX = _facing * _scale;
        Facing.ScaleY = _scale;
    }

    /// <summary>Swap the five cutout layers (legs / arms / body) to match the
    /// chosen character, and set each limb's rotation pivot (hip / shoulder).</summary>
    private void ApplyCharacterImage()
    {
        try
        {
            bool female = _cfg.Companion == CompanionGender.Female;
            string dir = female ? "female" : "male";

            var legL = Part(dir, "leg-l");
            var legR = Part(dir, "leg-r");
            var armL = Part(dir, "arm-l");
            var armR = Part(dir, "arm-r");
            var body = Part(dir, "body");

            // If ANY cutout layer failed to load we fall back to a single full
            // sprite so the wolf is always visible (a transparent window with
            // no art looked like "nothing happened" when waking him).
            if (legL == null || legR == null || armL == null || armR == null || body == null)
            {
                _simpleMode = true;
                LegL.Source = LegR.Source = ArmL.Source = ArmR.Source = null;
                LegLRot.Angle = LegRRot.Angle = ArmLRot.Angle = ArmRRot.Angle = 0;
                BodyImg.Source = FullSprite(female) ?? body;
                return;
            }

            _simpleMode = false;
            LegL.Source  = legL;
            LegR.Source  = legR;
            ArmL.Source  = armL;
            ArmR.Source  = armR;
            BodyImg.Source = body;

            // Rotation pivots as fractions of each full-canvas layer: legs swing
            // from the hip, arms from the shoulder. Values differ slightly per
            // sprite so the joints line up exactly.
            if (female)
            {
                LegL.RenderTransformOrigin = new Point(0.258, 0.598);
                LegR.RenderTransformOrigin = new Point(0.745, 0.598);
                ArmL.RenderTransformOrigin = new Point(0.343, 0.302);
                ArmR.RenderTransformOrigin = new Point(0.655, 0.302);
            }
            else
            {
                LegL.RenderTransformOrigin = new Point(0.265, 0.598);
                LegR.RenderTransformOrigin = new Point(0.752, 0.598);
                ArmL.RenderTransformOrigin = new Point(0.342, 0.302);
                ArmR.RenderTransformOrigin = new Point(0.654, 0.302);
            }
        }
        catch
        {
            // Last-ditch: show whatever full sprite we can so he is never blank.
            try
            {
                _simpleMode = true;
                LegL.Source = LegR.Source = ArmL.Source = ArmR.Source = null;
                BodyImg.Source = FullSprite(_cfg.Companion == CompanionGender.Female);
            }
            catch { }
        }
    }

    /// <summary>Load a cutout part, returning null (never throwing) if missing
    /// so the caller can fall back to a single full sprite.</summary>
    private static BitmapImage? Part(string dir, string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/parts/{dir}/{name}.png", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>The whole-wolf sprite used when the cutout parts are unavailable.</summary>
    private static BitmapImage? FullSprite(bool female)
    {
        foreach (var path in female
                     ? new[] { "wolf-female.png", "wolf-companion.png", "wolf-male.png" }
                     : new[] { "wolf-male.png", "wolf-companion.png", "wolf-female.png" })
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/{path}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = uri;
                bmp.EndInit();
                return bmp;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Reload settings after the control panel changed them.</summary>
    public void ReloadConfig()
    {
        _cfg = CompanionConfig.Load();
        ApplyConfig();
        if (_cfg.Enabled && !IsVisible) Show();
    }

    /// <summary>Explicit "Wake the wolf": make the companion visible and park him
    /// at a known on-screen spot (bottom-left of the primary work area) so he
    /// can never end up invisible or stranded off-screen.</summary>
    public void WakeOnScreen()
    {
        try
        {
            if (!IsVisible) Show();
            Visibility = Visibility.Visible;
            Opacity = 1.0;

            var wa = SystemParameters.WorkArea;
            Left = wa.Left + 60;
            Top = Math.Max(wa.Top, wa.Bottom - Height);

            StopPoses();
            _behaviour = Behaviour.Idle;
            _facing = 1;
            UpdateFacing();
            Bob.Y = 0; Tilt.Angle = 0;

            Topmost = false; Topmost = _cfg.AlwaysOnTop;
        }
        catch { }
    }

    // ===================== behaviour brain =====================
    private void BrainTick()
    {
        // Gaming mode: while a game is running we go quiet and still, keeping
        // only the critical temperature watch alive (handled in MonitorTick).
        bool wasGaming = _gaming;
        if (_cfg.GamingModeAuto) _gaming = IsGameRunning();
        if (_gaming)
        {
            if (!wasGaming) { StopPoses(); _behaviour = Behaviour.Sit; }
            return;
        }

        // Let a walk / timed pose finish before choosing something new.
        if (_busy || _behaviour == Behaviour.Walk) return;
        if (DateTime.Now < _nextDecision) return;

        long idleMs = IdleMilliseconds();

        // A long, uninterrupted session earns the occasional nudge.
        if (idleMs < 2000 && (DateTime.Now - _sessionStart).TotalMinutes > 90 &&
            (DateTime.Now - _lastLongSession).TotalMinutes > 20 && _cfg.SpeakChit)
        {
            _lastLongSession = DateTime.Now;
            Say(WolfLines.Pick(WolfLines.LongSession), WolfMood.Sleepy, 5);
        }

        // When the user has been idle a while the wolf gets bored: it either
        // settles down to sleep, or (if allowed) wanders off to roam the whole
        // desktop and bat at things for fun.
        if (idleMs > 90_000)
        {
            ChooseBored();
            return;
        }

        ChooseEveryday();
    }

    private void ChooseBored()
    {
        int r = _rng.Next(100);
        if (_cfg.PlayWhenBored && r < 55) StartPlay();
        else if (r < 80) StartSleep();
        else StartWalkRandom();
    }

    private void ChooseEveryday()
    {
        int r = _rng.Next(100);
        if (r < 34) StartWalkRandom();
        else if (r < 50) HoldPose(Behaviour.Look, 2.2, WolfMood.Happy);
        else if (r < 62) HoldPose(Behaviour.Sit, 3.0, WolfMood.Happy);
        else if (r < 72) HoldPose(Behaviour.Stretch, 1.6, WolfMood.Happy, pulse: true);
        else if (r < 82) HoldPose(Behaviour.Yawn, 1.6, WolfMood.Sleepy, pulse: true);
        else if (r < 92) HoldPose(Behaviour.Scratch, 1.8, WolfMood.Happy, wobble: true);
        else if (_cfg.SpeakChit && (DateTime.Now - _lastChit).TotalSeconds > 45)
        {
            _lastChit = DateTime.Now;
            Say(WolfLines.Pick(WolfLines.Random), WolfMood.Happy, 4, WolfClip.Random);
            ScheduleNext(2.5, 4.5);
        }
        else HoldPose(Behaviour.Idle, 2.0, _mood);
    }

    private void ScheduleNext(double minSec, double maxSec)
        => _nextDecision = DateTime.Now.AddSeconds(minSec + _rng.NextDouble() * (maxSec - minSec));

    // ----- idle input + gaming detection (Win32) -----
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static long IdleMilliseconds()
    {
        try
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii)) return GetTickCount() - lii.dwTime;
        }
        catch { }
        return 0;
    }

    /// <summary>Rough but reliable "is a full-screen game in front" check: the
    /// foreground window covers the whole primary screen and is not our window,
    /// the desktop or the shell.</summary>
    private bool IsGameRunning()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == new WindowInteropHelper(this).Handle) return false;
            if (fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            if (!GetWindowRect(fg, out RECT r)) return false;

            double w = r.Right - r.Left, h = r.Bottom - r.Top;
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            // Allow a few pixels of slack for borderless-fullscreen windows.
            return w >= sw - 4 && h >= sh - 4;
        }
        catch { return false; }
    }

    // ===================== poses & movement =====================
    private DispatcherTimer? _poseTimer;
    private bool _pokeOnArrive;

    private void StopPoses()
    {
        _poseTimer?.Stop();
        _busy = false;
        SleepZ.Visibility = Visibility.Collapsed;
    }

    private void HoldPose(Behaviour b, double seconds, WolfMood mood, bool pulse = false, bool wobble = false)
    {
        _busy = true;
        _behaviour = b;
        SetMood(mood);
        SleepZ.Visibility = Visibility.Collapsed;
        if (pulse) PlayPulse();
        if (wobble) PlayWobble();
        StartPoseTimer(seconds);
    }

    private void StartPoseTimer(double seconds)
    {
        _poseTimer?.Stop();
        _poseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _poseTimer.Tick += (_, _) => { _poseTimer!.Stop(); EndPose(); };
        _poseTimer.Start();
    }

    private void EndPose()
    {
        _busy = false;
        SleepZ.Visibility = Visibility.Collapsed;
        _behaviour = Behaviour.Idle;
        ScheduleNext(0.6, 2.2);
    }

    private void StartSleep()
    {
        _busy = true;
        _behaviour = Behaviour.Sleep;
        SetMood(WolfMood.Sleepy);
        SleepZ.Visibility = Visibility.Visible;
        // slow "breathing"
        var a = new DoubleAnimation(1.0, 1.03, TimeSpan.FromSeconds(2.4))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        Pulse.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, a);
        StartPoseTimer(6 + _rng.NextDouble() * 12);
    }

    private void StartWalkRandom()
    {
        var area = _cfg.RoamScreen
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                       SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;

        _targetX = area.Left + _rng.NextDouble() * Math.Max(1, area.Width - Width);
        if (_cfg.RoamScreen)
        {
            _targetY = area.Top + _rng.NextDouble() * Math.Max(1, area.Height - Height);
            _hasTargetY = true;
        }
        else _hasTargetY = false;

        _pokeOnArrive = false;
        FaceToward(_targetX);
        _behaviour = Behaviour.Walk;
        _busy = false;
    }

    private void StartPlay()
    {
        // "Play with the desktop": roam to a random spot anywhere on screen and
        // bat at it. This is purely cosmetic - the wolf never moves, deletes or
        // touches your real desktop icons or files.
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        _targetX = area.Left + _rng.NextDouble() * Math.Max(1, area.Width - Width);
        _targetY = area.Top + _rng.NextDouble() * Math.Max(1, area.Height - Height);
        _hasTargetY = true;
        _pokeOnArrive = true;
        FaceToward(_targetX);
        _behaviour = Behaviour.Walk;
        _busy = false;
    }

    private void FaceToward(double x)
    {
        _facing = x >= Left ? 1 : -1;
        UpdateFacing();
    }

    private void MoveTick()
    {
        bool walking = _behaviour == Behaviour.Walk;
        if (walking)
        {
            const double speed = 3.0;
            double dx = _targetX - Left;
            double dy = _hasTargetY ? _targetY - Top : 0;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist <= speed)
            {
                Left = _targetX;
                if (_hasTargetY) Top = _targetY;
                Bob.Y = 0; Tilt.Angle = 0;
                ArriveFromWalk();
            }
            else
            {
                double stepX = dx / dist * speed;
                double stepY = dy / dist * speed;
                Left += stepX;
                Top += stepY;
                _facing = stepX >= 0 ? 1 : -1;
                UpdateFacing();

                // little hop + tilt so the walk reads as a walk
                _bobPhase += 0.35;
                Bob.Y = -Math.Abs(Math.Sin(_bobPhase)) * 4;
                Tilt.Angle = Math.Sin(_bobPhase) * 2.5;
            }
        }

        AnimateLimbs(_behaviour == Behaviour.Walk);
    }

    /// <summary>Walk cycle: legs swing in antiphase and the arms swing opposite
    /// their same-side leg. The amplitude eases in while walking and smoothly
    /// settles back to a neutral stand when the wolf stops.</summary>
    private void AnimateLimbs(bool walking)
    {
        // In fallback mode there are no separate limb layers to swing.
        if (_simpleMode) return;

        double target = walking ? 1.0 : 0.0;
        _legSwing += (target - _legSwing) * 0.18;
        if (_legSwing < 0.001) _legSwing = 0;

        double s = Math.Sin(_bobPhase);
        double leg = s * 14.0 * _legSwing;   // degrees
        double arm = s * 11.0 * _legSwing;

        LegLRot.Angle =  leg;
        LegRRot.Angle = -leg;
        ArmLRot.Angle = -arm;   // arms opposite the same-side leg
        ArmRRot.Angle =  arm;
    }

    private void ArriveFromWalk()
    {
        _behaviour = Behaviour.Idle;
        if (_pokeOnArrive)
        {
            _pokeOnArrive = false;
            PlayPulse();
            if (_cfg.SpeakChit && _rng.Next(100) < 35 &&
                (DateTime.Now - _lastChit).TotalSeconds > 30)
            {
                _lastChit = DateTime.Now;
                Say(WolfLines.Pick(WolfLines.Random), WolfMood.Happy, 3.5, WolfClip.Random, _cfg.SpeakChit);
            }
        }
        ScheduleNext(0.5, 2.0);
    }

    // ===================== small animations =====================
    private void PlayPulse()
    {
        var a = new DoubleAnimation(1.0, 1.12, TimeSpan.FromMilliseconds(140)) { AutoReverse = true };
        Pulse.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, a);
        Pulse.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, a);
    }

    private void PlayWobble()
    {
        var a = new DoubleAnimation(-6, 6, TimeSpan.FromMilliseconds(120))
        { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        Tilt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, a);
    }

    private void SetMood(WolfMood mood)
    {
        _mood = mood;
        (Color c, double op) = mood switch
        {
            WolfMood.Happy   => (Color.FromRgb(0x34, 0xC7, 0x59), 0.28),
            WolfMood.Proud   => (Color.FromRgb(0xE8, 0x90, 0x2B), 0.45),
            WolfMood.Sleepy  => (Color.FromRgb(0x2B, 0xD4, 0xD9), 0.22),
            WolfMood.Annoyed => (Color.FromRgb(0xE8, 0x90, 0x2B), 0.40),
            WolfMood.Worried => (Color.FromRgb(0xFF, 0xB0, 0x20), 0.60),
            WolfMood.Angry   => (Color.FromRgb(0xFF, 0x4D, 0x4F), 0.85),
            _                => (Color.FromRgb(0xE8, 0x90, 0x2B), 0.30),
        };
        MoodGlow.Color = c;
        var fade = new DoubleAnimation(op, TimeSpan.FromMilliseconds(300));
        MoodGlow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
    }

    // ===================== voice =====================
    private void Say(string text, WolfMood mood, double seconds, bool speak = true)
        => Say(text, mood, seconds, null, speak);

    private void Say(string text, WolfMood mood, double seconds, WolfClip? clip, bool speak = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SetMood(mood);

        if (speak && _cfg.Voice != WolfVoiceStyle.Silent)
        {
            // Prefer the real recorded voice clip for this character; only fall
            // back to Windows text-to-speech when no recording is available.
            bool played = clip.HasValue && _audio.TryPlay(clip.Value);
            if (!played) _voice.Say(text);
            else _voice.StopSpeaking();
        }
    }

    private void Teardown()
    {
        try { _brain?.Stop(); _mover?.Stop(); _monitor?.Stop(); _poseTimer?.Stop(); } catch { }
        try { _voice.Dispose(); } catch { }
        try { _audio.Dispose(); } catch { }
    }

    // ===================== temperature watch =====================
    private void MonitorTick()
    {
        RigSnapshot snap;
        try { snap = _rig.Read(); } catch { return; }

        double? cpu = snap.CpuTempC;
        double? gpu = snap.GpuTempC;
        if (cpu == null && gpu == null) return;   // no readings on this PC

        bool cpuCrit = cpu is double c1 && c1 >= _cfg.CpuCriticalC;
        bool gpuCrit = gpu is double g1 && g1 >= _cfg.GpuCriticalC;
        bool cpuWarn = cpu is double c2 && c2 >= _cfg.CpuWarnC;
        bool gpuWarn = gpu is double g2 && g2 >= _cfg.GpuWarnC;

        Thermal now = (cpuCrit || gpuCrit) ? Thermal.Critical
                    : (cpuWarn || gpuWarn) ? Thermal.Warning
                    : Thermal.Normal;

        if (now == _thermal) return;   // only react to CHANGES, never spam
        var prev = _thermal;
        _thermal = now;

        switch (now)
        {
            case Thermal.Warning:
                SetMood(WolfMood.Worried);
                if (_cfg.AutoCooling && !_coolingEngaged)
                {
                    var r = _fans.RequestMoreCooling(maximum: false);
                    _coolingEngaged = r.ActionTaken;
                }
                // Gaming mode suppresses mere warnings - only criticals break in.
                if (!_gaming && _cfg.SpeakWarnings)
                    Say(WolfLines.Pick(WolfLines.TooHot), WolfMood.Worried, 5, WolfClip.Hot);
                break;

            case Thermal.Critical:
                SetMood(WolfMood.Angry);
                if (_cfg.AutoCooling)
                {
                    var r = _fans.RequestMoreCooling(maximum: true);
                    _coolingEngaged = r.ActionTaken;
                }
                if (_cfg.SpeakWarnings)
                {
                    string line = WolfLines.Pick(WolfLines.TooHot) +
                        "  Ease off the workload or shut down to be safe.";
                    Say(line, WolfMood.Angry, 7, WolfClip.Hot);
                }
                break;

            case Thermal.Normal:
                SetMood(WolfMood.Happy);
                if (_coolingEngaged) { _fans.RelaxCooling(); _coolingEngaged = false; }
                if (prev != Thermal.Normal && !_gaming && _cfg.SpeakChit)
                    Say(WolfLines.Pick(WolfLines.AllGood), WolfMood.Proud, 4, WolfClip.Good);
                break;
        }
    }

    /// <summary>Panel "Check my PC now" button: give an immediate spoken verdict.</summary>
    public void SpeakHealthSummary()
    {
        RigSnapshot snap;
        try { snap = _rig.Read(); } catch { Say("I can't read the sensors right now.", WolfMood.Annoyed, 4); return; }
        double? cpu = snap.CpuTempC, gpu = snap.GpuTempC;
        bool hot = (cpu is double c && c >= _cfg.CpuWarnC) || (gpu is double g && g >= _cfg.GpuWarnC);
        if (hot) Say(WolfLines.Pick(WolfLines.TooHot), WolfMood.Worried, 5, WolfClip.Hot);
        else Say(WolfLines.Pick(WolfLines.AllGood), WolfMood.Proud, 4, WolfClip.Good);
    }

    /// <summary>Shutdown farewell, said just before the app / companion closes.</summary>
    public void SayFarewell()
    {
        try { Say(WolfLines.Pick(WolfLines.Shutdown), WolfMood.Sleepy, 2.5, WolfClip.Shutdown); } catch { }
    }

    // ===================== mouse: drag, click, menu =====================
    private void OnWolfMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // double-click opens the control panel
            OpenPanelRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragMoved = false;
        _dragMouseStart = PointToScreen(e.GetPosition(this));
        _dragLeftStart = Left;
        _dragTopStart = Top;
        SpriteBox.CaptureMouse();
    }

    private void OnWolfMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = PointToScreen(e.GetPosition(this));
        double dx = p.X - _dragMouseStart.X;
        double dy = p.Y - _dragMouseStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;

        _dragMoved = true;
        // the user is carrying the wolf: stop whatever it was doing
        _behaviour = Behaviour.Idle;
        StopPoses();
        Left = _dragLeftStart + dx;
        Top = _dragTopStart + dy;
    }

    private void OnWolfMouseUp(object sender, MouseButtonEventArgs e)
    {
        SpriteBox.ReleaseMouseCapture();
        if (_dragging && !_dragMoved)
        {
            // a real click (not a drag): cheeky reaction
            PlayPulse();
            Say(WolfLines.Pick(WolfLines.Clicked), WolfMood.Annoyed, 3, WolfClip.Click);
            ScheduleNext(2.0, 3.5);
        }
        _dragging = false;
    }

    private void OnWolfRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        menu.Items.Add(MenuItem("Open control panel\u2026", (_, _) => OpenPanelRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(MenuItem("Open PC Care diagnostics\u2026", (_, _) => OpenDiagnosticsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("Check my PC now", (_, _) => SpeakHealthSummary()));
        menu.Items.Add(MenuItem("Bigger", (_, _) => Resize(+0.15)));
        menu.Items.Add(MenuItem("Smaller", (_, _) => Resize(-0.15)));
        bool isFemale = _cfg.Companion == CompanionGender.Female;
        menu.Items.Add(MenuItem(isFemale ? "Switch to the male wolf" : "Switch to the female wolf",
            (_, _) => SwitchCharacter(isFemale ? CompanionGender.Male : CompanionGender.Female)));
        bool muted = _cfg.Voice == WolfVoiceStyle.Silent;
        menu.Items.Add(MenuItem(muted ? "Unmute voice" : "Mute voice", (_, _) => ToggleMute()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(MenuItem("Hide for now", (_, _) => Hide()));
        menu.Items.Add(MenuItem("Close companion", (_, _) => { SayFarewell(); Close(); }));

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static System.Windows.Controls.MenuItem MenuItem(string header, RoutedEventHandler onClick)
    {
        var mi = new System.Windows.Controls.MenuItem { Header = header };
        mi.Click += onClick;
        return mi;
    }

    private void SwitchCharacter(CompanionGender gender)
    {
        _cfg.Companion = gender;
        _cfg.Save();
        _voice.CompanionFemale = gender == CompanionGender.Female;
        _audio.SetGender(gender);
        ApplyCharacterImage();
        // greet in the new voice so the change is obvious
        Say(WolfLines.Pick(WolfLines.Random), WolfMood.Happy, 3.5, WolfClip.Random);
    }

    private void Resize(double delta)
    {
        _cfg.Scale = Math.Max(0.6, Math.Min(1.8, _cfg.Scale + delta));
        _cfg.Save();
        ApplyConfig();
    }

    private void ToggleMute()
    {
        _cfg.Voice = _cfg.Voice == WolfVoiceStyle.Silent ? WolfVoiceStyle.Funny : WolfVoiceStyle.Silent;
        _cfg.Save();
        _voice.Style = _cfg.Voice;
        if (_cfg.Voice == WolfVoiceStyle.Silent) { _voice.StopSpeaking(); _audio.Stop(); }
    }
}
