namespace CalendarSaver;

/// <summary>
/// All on-disk locations. The .scr runs from System32, which is not writable,
/// so everything lives under the user profile.
/// </summary>
internal static class AppPaths
{
    public static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PactoTechCalendarSaver");

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PactoTechCalendarSaver");

    public static string WebView2UserData => Path.Combine(DataDir, "WebView2");
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string WebRoot => Path.Combine(DataDir, "web");
    public static string LogFile => Path.Combine(DataDir, "error.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(SettingsDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebRoot);
        MigrateLegacySettings();
    }

    /// <summary>Carries settings forward from the pre-rename "CalendarSaver" folder.</summary>
    private static void MigrateLegacySettings()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CalendarSaver", "settings.json");
            if (!File.Exists(SettingsFile) && File.Exists(legacy))
                File.Copy(legacy, SettingsFile);
        }
        catch { /* worst case the user re-enters settings */ }
    }

    public static void Log(string message)
    {
        try
        {
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 512 * 1024)
                File.Delete(LogFile); // simple rotation so the log never grows unbounded
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { /* logging must never crash the saver */ }
    }
}
