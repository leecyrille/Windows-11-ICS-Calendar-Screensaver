using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CalendarSaver;

/// <summary>Fullscreen WebView2 host on the primary monitor. All rendering and input
/// detection happens in the embedded page; the page posts {type:'exit'} to close.</summary>
public class ScreensaverForm : Form
{
    private readonly WebView2 _webView = new();
    private readonly AppSettings _settings;
    private readonly FeedService _feedService;
    private readonly PhotoService _photoService = new();

    private FeedResult? _lastFeeds;
    private List<PhotoDto> _photos = new();
    private System.Windows.Forms.Timer? _refreshTimer;
    private System.Windows.Forms.Timer? _photoTimer;
    private System.Windows.Forms.Timer? _midnightTimer;
    private System.Windows.Forms.Timer? _themeTimer;
    private string? _lastTheme;
    private bool _refreshing;

    private readonly bool _windowed;
    private readonly RenderOptions? _render;

    public ScreensaverForm(AppSettings settings, bool windowed = false, RenderOptions? render = null)
    {
        _settings = settings;
        _render = render;
        _windowed = windowed || render != null;
        _feedService = new FeedService(settings);

        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = render != null
            // Off every monitor: it still renders, but nobody sees it.
            ? new Rectangle(SystemInformation.VirtualScreen.Right + 200, SystemInformation.VirtualScreen.Top, render.Width, render.Height)
            : windowed ? new Rectangle(bounds.X + 80, bounds.Y + 80, 1920, 1080) : bounds;
        TopMost = !_windowed;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(13, 17, 23);

        _webView.DefaultBackgroundColor = Color.FromArgb(13, 17, 23);
        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        // Fallback input handling on the host form. When the WebView has focus it swallows
        // all input and the page's JS handles exiting; these only fire if focus ever ends up
        // on the form itself (e.g. before the page is ready).
        KeyPreview = true;
        KeyDown += (_, _) => { if (!_windowed) ExitFromHost("host keydown"); };
        MouseDown += (_, _) => { if (!_windowed) ExitFromHost("host mousedown"); };
        MouseWheel += (_, _) => { if (!_windowed) ExitFromHost("host wheel"); };
        MouseMove += (_, e) =>
        {
            if (_windowed) return;
            if (_hostLastMouse is { } last)
            {
                _hostMouseTravel += Math.Sqrt(Math.Pow(e.X - last.X, 2) + Math.Pow(e.Y - last.Y, 2));
                if (_hostMouseTravel > 10) ExitFromHost("host mousemove");
            }
            _hostLastMouse = new Point(e.X, e.Y);
        };
    }

    private Point? _hostLastMouse;
    private double _hostMouseTravel;

    // Render mode must never take focus from whatever the user is doing.
    protected override bool ShowWithoutActivation => _render != null;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_render != null) cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
            return cp;
        }
    }

    private static void ExitFromHost(string reason)
    {
        AppPaths.Log("Exit requested: " + reason);
        ExitSaver();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_windowed) Cursor.Hide();
        if (_render == null) Activate();
        try
        {
            await InitWebViewAsync();
        }
        catch (Exception ex)
        {
            AppPaths.Log("WebView2 init failed: " + ex);
            ExitSaver();
        }
    }

    private bool _fileFallback;
    private CoreWebView2Environment? _environment;

    private async Task InitWebViewAsync()
    {
        // The user data folder MUST be explicit: the .scr runs from System32,
        // and WebView2's default (next to the exe) would crash.
        // Render mode gets its own profile and web folder so it can run while the real
        // screensaver starts. Off-screen windows count as hidden to Chromium, which would
        // stop painting them: turn that off.
        var userData = _render != null ? AppPaths.WebView2UserData + "-render" : AppPaths.WebView2UserData;
        var webRoot = _render != null ? AppPaths.WebRoot + "-render" : AppPaths.WebRoot;
        Directory.CreateDirectory(webRoot);
        var options = _render != null
            ? new CoreWebView2EnvironmentOptions("--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows --disable-renderer-backgrounding")
            : null;
        _environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
        var environment = _environment;
        AppPaths.Log($"WebView2 runtime {environment.BrowserVersionString}");
        await _webView.EnsureCoreWebView2Async(environment);
        // One CSS pixel per image pixel, whatever the display scaling.
        if (_render != null) _webView.ZoomFactor = 96.0 / DeviceDpi;

        var core = _webView.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsPinchZoomEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif

        WebAssets.ExtractTo(webRoot);
        AppPaths.Log($"Extracted web assets to {webRoot}");
        core.SetVirtualHostNameToFolderMapping("app", webRoot, CoreWebView2HostResourceAccessKind.Allow);

        // Photos are served by intercepting https://photosN/... requests and streaming the
        // file ourselves. Unlike SetVirtualHostNameToFolderMapping this cannot be broken by
        // security software, and it works no matter which origin the page itself runs on.
        core.AddWebResourceRequestedFilter("https://photos*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, args) =>
        {
            if (_render == null) _webView.Focus(); // keyboard must land in the page
            if (args.IsSuccess) return;
            AppPaths.Log($"Navigation failed: {args.WebErrorStatus}" + (_fileFallback ? " (already in fallback)" : "; retrying via file://"));
            if (!_fileFallback)
            {
                // Virtual-host mapping can be unavailable (e.g. blocked by security software);
                // the extracted page works over plain file:// too, with file:// photo URLs.
                _fileFallback = true;
                core.Navigate(new Uri(Path.Combine(webRoot, "index.html")).AbsoluteUri);
            }
        };
        core.Navigate("https://app/index.html");
        if (_render == null) _webView.Focus();
    }

    /// <summary>Streams https://photosN/rel/path.jpg requests from the configured folders.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.StartsWith("photos", StringComparison.OrdinalIgnoreCase)) return;
            if (!int.TryParse(uri.Host["photos".Length..], out var index) ||
                index < 0 || index >= _settings.PhotoFolders.Count)
            {
                e.Response = _environment!.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var root = Path.GetFullPath(_settings.PhotoFolders[index]);
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', '\\');
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                e.Response = _environment!.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }

            var contentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };
            var stream = File.OpenRead(full);
            e.Response = _environment!.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {contentType}");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Photo serve failed: " + ex.Message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "exit":
                    if (_windowed) break; // screenshot/dev mode: input never exits
                    var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    AppPaths.Log("Exit requested by page: " + (reason ?? "(no reason)"));
                    ExitSaver();
                    break;
                case "ready":
                    _ = StartupAsync();
                    break;
                case "metrics": // page-side diagnostics
                    AppPaths.Log("Page metrics: " + e.WebMessageAsJson);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Bad web message: " + ex.Message);
        }
    }

    private async Task StartupAsync()
    {
        try
        {
            // Fast first paint from the on-disk cache, then a real network refresh.
            _lastFeeds = await _feedService.FetchAllAsync(cacheOnly: true);
            _photos = _photoService.Scan(_settings.PhotoFolders);
            PushPayload();
            StartTimers();
            if (_render != null) _ = CaptureLoopAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Startup failed: " + ex);
        }
    }

    /// <summary>Render mode: save the page as an image just after every interval boundary
    /// (so the clock reads right), until the parent process goes away.</summary>
    private async Task CaptureLoopAsync()
    {
        var render = _render!;
        var watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        watchdog.Tick += (_, _) =>
        {
            if (render.ParentPid is int pid && !ProcessAlive(pid))
            {
                AppPaths.Log($"Render: parent {pid} is gone; exiting");
                ExitSaver();
            }
        };
        watchdog.Start();

        await Task.Delay(6000); // first paint, fonts, and the staggered photo fill
        while (true)
        {
            await CaptureAsync(render);
            var every = Math.Max(10, render.EverySeconds);
            var secs = DateTime.Now.TimeOfDay.TotalSeconds;
            var next = (Math.Floor(secs / every) + 1) * every + 1;
            await Task.Delay(TimeSpan.FromSeconds(next - secs));
        }
    }

    private async Task CaptureAsync(RenderOptions render)
    {
        var core = _webView.CoreWebView2;
        if (core == null) return;
        try
        {
            await core.ExecuteScriptAsync("typeof updateClock === 'function' && updateClock()");
            var jpeg = !render.OutPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            var tmp = render.OutPath + ".tmp";
            using (var fs = File.Create(tmp))
            {
                await core.CapturePreviewAsync(jpeg ? CoreWebView2CapturePreviewImageFormat.Jpeg : CoreWebView2CapturePreviewImageFormat.Png, fs);
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, render.OutPath, overwrite: true); break; }
                catch (IOException) when (attempt < 5) { await Task.Delay(200); } // reader has it open
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Render capture failed: " + ex.Message);
        }
    }

    private static bool ProcessAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private void StartTimers()
    {
        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1, _settings.RefreshMinutes) * 60_000,
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        _photoTimer = new System.Windows.Forms.Timer { Interval = 30 * 60_000 };
        _photoTimer.Tick += (_, _) =>
        {
            _photos = _photoService.Scan(_settings.PhotoFolders);
            PushPayload();
        };
        _photoTimer.Start();

        _themeTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _themeTimer.Tick += (_, _) =>
        {
            if (CurrentTheme() != _lastTheme) PushPayload();
        };
        _themeTimer.Start();

        ScheduleMidnightRollover();
    }

    private void ScheduleMidnightRollover()
    {
        var untilMidnight = DateTime.Today.AddDays(1).AddSeconds(10) - DateTime.Now;
        _midnightTimer?.Dispose();
        _midnightTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)Math.Clamp(untilMidnight.TotalMilliseconds, 1000, int.MaxValue),
        };
        _midnightTimer.Tick += async (_, _) =>
        {
            ScheduleMidnightRollover();
            await RefreshAsync(); // re-expands the (possibly new) month window and re-pushes
        };
        _midnightTimer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _lastFeeds = await _feedService.FetchAllAsync(cacheOnly: false);
            PushPayload();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Refresh failed: " + ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void PushPayload()
    {
        if (_webView.CoreWebView2 == null || _lastFeeds == null) return;
        var payload = PayloadBuilder.Build(_settings, _lastFeeds, _photos, DateTime.Now);
        payload.Theme = CurrentTheme();
        _lastTheme = payload.Theme;
        AppPaths.Log($"Push: {payload.Events.Count} events, {payload.Tasks.Count} tasks, " +
                     $"{payload.Photos.Count} photos, refresh={payload.LastRefresh ?? "null"}, " +
                     $"form={Bounds.Width}x{Bounds.Height}, dpi={DeviceDpi}");
        _webView.CoreWebView2.PostWebMessageAsJson(PayloadBuilder.ToJson(payload));
    }

    /// <summary>Render mode can fix the theme (--theme dark|light); otherwise the saver's own setting.</summary>
    private string CurrentTheme() =>
        _render?.Theme is "dark" or "light" ? _render.Theme : _settings.EffectiveTheme(DateTime.Now);

    private static void ExitSaver()
    {
        Cursor.Show();
        Application.Exit();
    }
}

/// <summary>Render mode (/p render): where to save the image, its size, how often, and which process to outlive.</summary>
public record RenderOptions(string OutPath, int Width, int Height, int EverySeconds, int? ParentPid, string? Theme = null);

/// <summary>Plain black topmost cover for each non-primary monitor. Handles its own
/// input (no WebView here) with the same 10px mouse-jitter tolerance.</summary>
public class BlackoutForm : Form
{
    private Point? _lastMouse;
    private double _accumulated;

    public BlackoutForm(Screen screen)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        KeyPreview = true;

        KeyDown += (_, _) => Quit();
        MouseDown += (_, _) => Quit();
        MouseWheel += (_, _) => Quit();
        MouseMove += (_, e) =>
        {
            if (_lastMouse is { } last)
            {
                _accumulated += Math.Sqrt(Math.Pow(e.X - last.X, 2) + Math.Pow(e.Y - last.Y, 2));
                if (_accumulated > 10) Quit();
            }
            _lastMouse = new Point(e.X, e.Y);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Cursor.Hide();
    }

    private static void Quit()
    {
        Cursor.Show();
        Application.Exit();
    }
}

internal static class WebAssets
{
    /// <summary>Extracts the embedded wwwroot resources ("web/*") to a writable folder so the
    /// page can be served via a virtual host (needed for the woff2 font and photo hosts).</summary>
    public static void ExtractTo(string directory)
    {
        var assembly = typeof(WebAssets).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("web/", StringComparison.Ordinal)) continue;
            var fileName = name["web/".Length..];
            using var source = assembly.GetManifestResourceStream(name)!;
            using var destination = File.Create(Path.Combine(directory, fileName));
            source.CopyTo(destination);
        }
    }
}
