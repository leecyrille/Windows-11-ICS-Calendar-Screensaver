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
