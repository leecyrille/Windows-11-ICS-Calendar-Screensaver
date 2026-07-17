using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarSaver;

public class FeedConfig
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7aa2f7";
    public bool Enabled { get; set; } = true;   // absent in older settings.json → defaults to shown
}

public class AppSettings
{
    public List<FeedConfig> Feeds { get; set; } = new();
    public List<string> PhotoFolders { get; set; } = new();
    public int PhotoIntervalSeconds { get; set; } = 20;
    public int RefreshMinutes { get; set; } = 15;
    public string Theme { get; set; } = "dark";   // "dark" | "light"
    public string? DarkStart { get; set; }        // "HH:mm"; with DarkEnd set, dark runs start → end
    public string? DarkEnd { get; set; }          // and the fixed Theme choice is ignored

    /// <summary>The theme to show right now: scheduled dark window (possibly crossing
    /// midnight) when configured, otherwise the fixed theme choice.</summary>
    public string EffectiveTheme(DateTime now)
    {
        if (TimeSpan.TryParse(DarkStart ?? "", out var start) &&
            TimeSpan.TryParse(DarkEnd ?? "", out var end) && start != end)
        {
            var t = now.TimeOfDay;
            var dark = start < end ? t >= start && t < end : t >= start || t < end;
            return dark ? "dark" : "light";
        }
        return Theme == "light" ? "light" : "dark";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOpts);
                if (loaded != null)
                {
                    loaded.PhotoIntervalSeconds = Math.Clamp(loaded.PhotoIntervalSeconds, 3, 3600);
                    loaded.RefreshMinutes = Math.Clamp(loaded.RefreshMinutes, 1, 1440);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Failed to load settings: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
