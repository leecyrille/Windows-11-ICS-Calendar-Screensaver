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

    public ScreensaverForm(AppSettings settings, bool windowed = false)
    {
        _settings = settings;
        _windowed = windowed;
        _feedService = new FeedService(settings);

        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = windowed ? new Rectangle(bounds.X + 80, bounds.Y + 80, 1920, 1080) : bounds;
        TopMost = !windowed;
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

    private static void ExitFromHost(string reason)
    {
        AppPaths.Log("Exit requested: " + reason);
        ExitSaver();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (!_windowed) Cursor.Hide();
        Activate();
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
        _environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserData);
        var environment = _environment;
        AppPaths.Log($"WebView2 runtime {environment.BrowserVersionString}");
        await _webView.EnsureCoreWebView2Async(environment);

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

        WebAssets.ExtractTo(AppPaths.WebRoot);
        AppPaths.Log($"Extracted web assets to {AppPaths.WebRoot}");
        core.SetVirtualHostNameToFolderMapping("app", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.Allow);

        // Photos are served by intercepting https://photosN/... requests and streaming the
        // file ourselves. Unlike SetVirtualHostNameToFolderMapping this cannot be broken by
        // security software, and it works no matter which origin the page itself runs on.
        core.AddWebResourceRequestedFilter("https://photos*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, args) =>
        {
            _webView.Focus(); // keyboard must land in the page
            if (args.IsSuccess) return;
            AppPaths.Log($"Navigation failed: {args.WebErrorStatus}" + (_fileFallback ? " (already in fallback)" : "; retrying via file://"));
            if (!_fileFallback)
            {
                // Virtual-host mapping can be unavailable (e.g. blocked by security software);
                // the extracted page works over plain file:// too, with file:// photo URLs.
                _fileFallback = true;
                core.Navigate(new Uri(Path.Combine(AppPaths.WebRoot, "index.html")).AbsoluteUri);
            }
        };
        core.Navigate("https://app/index.html");
        _webView.Focus();
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
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Startup failed: " + ex);
        }
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
            if (_settings.EffectiveTheme(DateTime.Now) != _lastTheme) PushPayload();
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
        _lastTheme = payload.Theme;
        AppPaths.Log($"Push: {payload.Events.Count} events, {payload.Tasks.Count} tasks, " +
                     $"{payload.Photos.Count} photos, refresh={payload.LastRefresh ?? "null"}, " +
                     $"form={Bounds.Width}x{Bounds.Height}, dpi={DeviceDpi}");
        _webView.CoreWebView2.PostWebMessageAsJson(PayloadBuilder.ToJson(payload));
    }

    private static void ExitSaver()
    {
        Cursor.Show();
        Application.Exit();
    }
}

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
