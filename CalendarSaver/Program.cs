namespace CalendarSaver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureDirectories();

        // Windows may pass "/s", "/S", "/c:12345", "/p 12345" etc. — decide on the first two chars.
        var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/c";
        if (mode.Length > 2) mode = mode[..2];

        try
        {
            switch (mode)
            {
                case "/s":
                    RunScreensaver();
                    break;

                case "/w": // debug: windowed, non-topmost, ignores input-exit — for screenshots/dev
                    Application.Run(new ScreensaverForm(AppSettings.Load(), windowed: true));
                    break;

                case "/p": // preview inside the tiny Settings-dialog monitor: not supported, exit quietly
                    // "/p render ..." draws the calendar off-screen for TVs (see RunRender). It rides on
                    // /p so older versions, which exit quietly here, never pop up their settings window.
                    if (args.Length > 2 && args[1].Equals("render", StringComparison.OrdinalIgnoreCase))
                        RunRender(args[1..]);
                    break;

                case "/d": // debug helper: dump the JSON payload the page would receive
                    DumpPayload(args.Length > 1 ? args[1] : Path.Combine(AppPaths.DataDir, "payload.json"));
                    break;

                case "/c":
                default:
                    using (var dialog = new SettingsForm())
                        dialog.ShowDialog();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Fatal: " + ex);
        }
    }

    private static void RunScreensaver()
    {
        var settings = AppSettings.Load();
        var main = new ScreensaverForm(settings);
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.Primary) continue;
            new BlackoutForm(screen).Show();
        }
        Application.Run(main);
    }

    /// <summary>/p render out.jpg [--size 1920x1080] [--every 60] [--theme dark|light] [--parent PID]: saves the calendar
    /// as a picture every interval from an invisible window, until the parent process exits.
    /// Used by Unofficial Google Home Volume Sync to show the calendar on TVs.</summary>
    private static void RunRender(string[] args)
    {
        if (args.Length < 2) return;
        int width = 1920, height = 1080, every = 60;
        int? parent = null;
        string? theme = null;
        for (var i = 2; i + 1 < args.Length; i += 2)
        {
            var value = args[i + 1];
            switch (args[i].ToLowerInvariant())
            {
                case "--size":
                    var wh = value.ToLowerInvariant().Split('x');
                    if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h))
                    {
                        width = Math.Clamp(w, 320, 7680);
                        height = Math.Clamp(h, 240, 4320);
                    }
                    break;
                case "--every":
                    if (int.TryParse(value, out var n)) every = Math.Clamp(n, 10, 3600);
                    break;
                case "--theme": // dark | light; default follows the saver's settings
                    theme = value.ToLowerInvariant() is "dark" or "light" ? value.ToLowerInvariant() : null;
                    break;
                case "--parent":
                    if (int.TryParse(value, out var pid)) parent = pid;
                    break;
            }
        }
        var outPath = Path.GetFullPath(args[1]);
        AppPaths.Log($"Render: {width}x{height} every {every}s to {outPath}");
        Application.Run(new ScreensaverForm(AppSettings.Load(), render: new RenderOptions(outPath, width, height, every, parent, theme)));
    }

    private static void DumpPayload(string outPath)
    {
        var settings = AppSettings.Load();
        var feeds = new FeedService(settings);
        var result = feeds.FetchAllAsync(cacheOnly: false).GetAwaiter().GetResult();
        var photos = new PhotoService().Scan(settings.PhotoFolders);
        var payload = PayloadBuilder.Build(settings, result, photos, DateTime.Now);
        File.WriteAllText(outPath, PayloadBuilder.ToJson(payload, indented: true));
    }
}
