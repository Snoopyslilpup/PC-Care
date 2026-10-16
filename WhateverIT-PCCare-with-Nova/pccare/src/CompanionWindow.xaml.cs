using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace WhateverITPCCare;

/// <summary>
/// A frameless, transparent, always-on-top overlay window that hosts the
/// desktop companion ("Nova", the rigged 3D wolf) in a WebView2. The wolf is
/// rendered and animated by a small local three.js page; this window just gives
/// it a see-through, click-through surface to live on so it never blocks the
/// user's work. The C# side moves the window around the screen (walking) and
/// pushes commands - play an animation, face a direction, speak - into the page.
/// </summary>
public partial class CompanionWindow : Window
{
    private bool _ready;
    private readonly TaskCompletionSource<bool> _readyTcs = new();

    /// <summary>Completes once the 3D model has loaded and the page is ready.
    /// Result is true on success, false if the page/model reported a load error.</summary>
    public Task<bool> Ready => _readyTcs.Task;

    /// <summary>Human-readable reason the companion failed to appear, or null if OK.
    /// Surfaced in the settings card and written to the diagnostic log.</summary>
    public string? LastError { get; private set; }

    /// <summary>Where diagnostic lines are appended so a stuck companion can be
    /// diagnosed from one file: %AppData%\WhateverITPCCare\companion-log.txt</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhateverITPCCare", "companion-log.txt");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void Fail(string reason)
    {
        LastError = reason;
        Log("FAIL: " + reason);
        if (!_readyTcs.Task.IsCompleted) _readyTcs.TrySetResult(false);
    }

    /// <summary>Raised when the user clicks the wolf (page reports a pointer hit).
    /// Only fires if the window is made interactive; click-through by default.</summary>
    public event Action? Clicked;

    public CompanionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyOverlayStyles();
    }

    // ===== Win32 overlay styling: tool window, no activation, click-through =====
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED     = 0x80000;
    private const int WS_EX_TOOLWINDOW  = 0x80;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private void ApplyOverlayStyles()
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    public async Task InitializeAsync()
    {
        try
        {
            // ---- Pre-flight: the companion files MUST be installed next to the exe.
            // This is the single most common reason Nova never appears (the
            // installer or build didn't ship Assets\companion). Check first and
            // fail LOUDLY (into the log + LastError) instead of hanging silently.
            string folder = Path.Combine(AppContext.BaseDirectory, "Assets", "companion");
            string indexPath = Path.Combine(folder, "index.html");
            string glbPath   = Path.Combine(folder, "wolf.glb");
            Log($"Init. BaseDirectory={AppContext.BaseDirectory}");
            Log($"Looking for companion assets in: {folder}");
            if (!Directory.Exists(folder))
            {
                Fail($"Companion assets folder is missing: {folder}. " +
                     "The build/installer did not ship Assets\\companion. " +
                     "Nova cannot render without wolf.glb + index.html.");
                return;
            }
            if (!File.Exists(indexPath))
            {
                Fail($"index.html is missing from {folder}.");
                return;
            }
            if (!File.Exists(glbPath))
            {
                Fail($"wolf.glb (the 3D model) is missing from {folder}.");
                return;
            }
            Log($"Assets OK (wolf.glb = {new FileInfo(glbPath).Length} bytes).");

            // Keep WebView2's user data under the app's AppData folder.
            string udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WhateverITPCCare", "companion-webview");
            Directory.CreateDirectory(udf);
            // A transparent, layered WPF overlay (AllowsTransparency=True) hosting a
            // GPU-composited WebView2 tears into garbled/noisy output on many GPUs:
            // the hardware composition surface can't be alpha-blended into the
            // layered window. Forcing SOFTWARE composition (WebGL itself still runs
            // on the GPU) blends cleanly into the see-through window. Negligible
            // cost for our tiny canvas, and it removes the corruption.
            var opts = new CoreWebView2EnvironmentOptions("--disable-gpu-compositing");
            var env = await CoreWebView2Environment.CreateAsync(null, udf, opts);
            await Web.EnsureCoreWebView2Async(env);
            Log("WebView2 runtime ready: " + CoreWebView2Environment.GetAvailableBrowserVersionString());

            var core = Web.CoreWebView2;
            Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsZoomControlEnabled = false;

            // Report any page-side JS error (e.g. the .glb failed to parse) so a
            // stuck companion leaves a trail instead of hanging forever.
            core.NavigationCompleted += (_, ev) =>
            {
                if (!ev.IsSuccess)
                    Fail($"Page navigation failed (WebErrorStatus={ev.WebErrorStatus}). " +
                         "The virtual-host mapping or index.html could not be loaded.");
            };
            core.ProcessFailed += (_, ev) =>
                Fail($"WebView2 process failed: {ev.ProcessFailedKind}.");

            // Serve the companion folder (next to the exe) over a virtual https host
            // so the page runs in a secure context (needed for smooth speech + no
            // file:// CORS issues loading three.js and the .glb).
            core.SetVirtualHostNameToFolderMapping(
                "companion.local", folder, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            Log("Navigating to https://companion.local/index.html");
            core.Navigate("https://companion.local/index.html");
        }
        catch (Exception ex)
        {
            Fail("WebView2 could not start: " + ex.Message +
                 " (is the Microsoft Edge WebView2 Runtime installed?)");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString() ?? e.WebMessageAsJson; }
        catch { return; }
        if (string.IsNullOrEmpty(json)) return;
        if (json.Contains("\"ready\""))
        {
            _ready = true;
            LastError = null;
            Log("Page reported READY (3D model loaded). anims=" + json);
            if (!_readyTcs.Task.IsCompleted) _readyTcs.TrySetResult(true);
        }
        else if (json.Contains("\"error\""))
        {
            // The three.js page couldn't load/parse wolf.glb.
            Fail("The 3D model failed to load in the page: " + json);
        }
        else if (json.Contains("\"click\""))
        {
            Clicked?.Invoke();
        }
    }

    // ===== command surface (safe no-ops until the page is ready) =====
    private async Task Exec(string js)
    {
        if (!_ready || Web.CoreWebView2 == null) return;
        try { await Web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    public Task PlayAsync(string state) => Exec($"Companion.play({JsStr(state)})");
    public Task FaceAsync(int dir)      => Exec($"Companion.face({dir})");
    public Task HushAsync()             => Exec("Companion.hush()");

    /// <summary>Show a speech bubble and speak it. When <paramref name="cat"/> is
    /// one of the recorded voice categories (good/hot/click/random/startup/
    /// shutdown) the matching recorded line plays; otherwise the browser voice
    /// reads the text.</summary>
    public Task SpeakAsync(string text, string tag = "Nova", string mood = "", string cat = "")
        => Exec($"Companion.speak({JsStr(text)},{JsStr(tag)},{JsStr(mood)},{JsStr(cat)})");

    /// <summary>Play a recorded voice line with no custom bubble text.</summary>
    public Task VoiceAsync(string cat) => Exec($"Companion.voice({JsStr(cat)})");

    /// <summary>Pick the recorded voice set: "male" or "female".</summary>
    public Task SetVoiceAsync(string gender) => Exec($"Companion.setVoice({JsStr(gender)})");

    private static string JsStr(string s)
    {
        s ??= "";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                      .Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
