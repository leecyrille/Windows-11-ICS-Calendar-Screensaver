using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarSaver;

/// <summary>An expanded event occurrence. For all-day events Start/End are inclusive dates ("yyyy-MM-dd");
/// for timed events they are local ISO date-times ("yyyy-MM-ddTHH:mm:ss").</summary>
public record EventDto(int Feed, string Title, bool AllDay, string Start, string End);

/// <summary>An incomplete task. Due is an inclusive local date ("yyyy-MM-dd") or null for undated.</summary>
public record TaskDto(int Feed, string Title, string? Due);

public record FeedStatusDto(string Name, string Color, bool Stale, string? Error, bool Enabled = true);

public record PhotoDto(int Folder, string RelativePath);

public class FeedResult
{
    public List<EventDto> Events { get; } = new();
    public List<TaskDto> Tasks { get; } = new();
    public List<FeedStatusDto> Statuses { get; } = new();
    public DateTime? LastNetworkRefresh { get; set; }
}

public class Payload
{
    public string Type { get; set; } = "data";
    public int Year { get; set; }
    public int Month { get; set; }
    public List<EventDto> Events { get; set; } = new();
    public List<TaskDto> Tasks { get; set; } = new();
    public List<FeedStatusDto> Feeds { get; set; } = new();
    public List<string> Photos { get; set; } = new();
    public int PhotoIntervalSeconds { get; set; } = 20;
    public string? LastRefresh { get; set; }
    public string Theme { get; set; } = "dark";
}

public static class PayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Payload Build(AppSettings settings, FeedResult feeds, List<PhotoDto> photos, DateTime now)
    {
        return new Payload
        {
            Year = now.Year,
            Month = now.Month,
            Events = feeds.Events,
            Tasks = feeds.Tasks,
            Feeds = feeds.Statuses,
            Photos = photos.Select(PhotoUrl).ToList(),
            PhotoIntervalSeconds = settings.PhotoIntervalSeconds,
            LastRefresh = feeds.LastNetworkRefresh?.ToString("HH:mm"),
            Theme = settings.EffectiveTheme(now),
        };
    }

    public static string ToJson(Payload payload, bool indented = false)
    {
        return JsonSerializer.Serialize(payload, indented
            ? new JsonSerializerOptions(JsonOpts) { WriteIndented = true }
            : JsonOpts);
    }

    /// <summary>Maps a scanned photo to its served URL (https://photosN/rel/path.jpg — see the
    /// WebResourceRequested handler in ScreensaverForm, which streams these from disk).</summary>
    private static string PhotoUrl(PhotoDto p)
    {
        var segments = p.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"https://photos{p.Folder}/{string.Join('/', segments)}";
    }
}
