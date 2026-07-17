using System.Security.Cryptography;
using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;

namespace CalendarSaver;

/// <summary>
/// Seam for task providers. v1 ships an ICS/VTODO source only; a Google Tasks API
/// source can be added later by implementing this interface (Google Calendar ICS
/// exports do NOT include Google Tasks).
/// </summary>
public interface ITaskSource
{
    Task<IReadOnlyList<TaskDto>> GetTasksAsync(CancellationToken ct = default);
}

/// <summary>Extracts incomplete VTODO components from already-parsed ICS calendars.</summary>
public class IcsTaskSource : ITaskSource
{
    private readonly IReadOnlyList<(Calendar Cal, int FeedIndex)> _calendars;

    public IcsTaskSource(IReadOnlyList<(Calendar, int)> calendars) => _calendars = calendars;

    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(CancellationToken ct = default)
    {
        var tasks = new List<TaskDto>();
        foreach (var (cal, feedIndex) in _calendars)
        {
            foreach (var todo in cal.Todos)
            {
                var status = todo.Status ?? "";
                if (status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) continue;
                if (status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
                if (todo.Completed != null) continue;

                var title = string.IsNullOrWhiteSpace(todo.Summary) ? "(untitled task)" : todo.Summary.Trim();
                var due = todo.Due is { } d ? FeedService.ToLocal(d).ToString("yyyy-MM-dd") : null;
                tasks.Add(new TaskDto(feedIndex, title, due));
            }
        }
        return Task.FromResult<IReadOnlyList<TaskDto>>(tasks);
    }
}

public class FeedService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly AppSettings _settings;

    public FeedService(AppSettings settings) => _settings = settings;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CalendarSaver/1.0");
        return client;
    }

    /// <summary>
    /// Fetches every configured feed, expands recurrences over the visible month ± 1 week,
    /// and collects VTODO tasks. With cacheOnly, only the on-disk cache is read (fast first paint);
    /// otherwise the network is tried first and the cache is the fallback (feed marked stale).
    /// </summary>
    public async Task<FeedResult> FetchAllAsync(bool cacheOnly)
    {
        var result = new FeedResult();
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var windowStart = monthStart.AddDays(-7);
        var windowEnd = monthStart.AddMonths(1).AddDays(7);

        var parsed = new List<(Calendar, int)>();

        for (var i = 0; i < _settings.Feeds.Count; i++)
        {
            var feed = _settings.Feeds[i];
            var name = string.IsNullOrWhiteSpace(feed.Name) ? $"Feed {i + 1}" : feed.Name.Trim();
            if (!feed.Enabled || string.IsNullOrWhiteSpace(feed.Url))
            {
                // Keep a status entry so event feed-indices stay aligned with the feeds list.
                result.Statuses.Add(new FeedStatusDto(name, feed.Color, false, null, false));
                continue;
            }

            string? ics = null;
            var stale = false;
            string? error = null;
            var cachePath = CachePathFor(feed.Url);

            if (!cacheOnly)
            {
                try
                {
                    ics = await FetchTextAsync(feed.Url);
                    await File.WriteAllTextAsync(cachePath, ics);
                }
                catch (Exception ex)
                {
                    error = Shorten(ex);
                }
            }

            if (ics == null && File.Exists(cachePath))
            {
                try
                {
                    ics = await File.ReadAllTextAsync(cachePath);
                    stale = !cacheOnly;
                }
                catch (Exception ex) { error ??= Shorten(ex); }
            }

            if (ics != null)
            {
                try
                {
                    var cal = Calendar.Load(ics);
                    parsed.Add((cal, i));
                    AddEvents(result.Events, cal, i, windowStart, windowEnd);
                }
                catch (Exception ex)
                {
                    error = "parse: " + Shorten(ex);
                }
            }

            result.Statuses.Add(new FeedStatusDto(name, feed.Color, stale, error));
        }

        ITaskSource taskSource = new IcsTaskSource(parsed);
        result.Tasks.AddRange(await taskSource.GetTasksAsync());

        if (!cacheOnly) result.LastNetworkRefresh = DateTime.Now;
        return result;
    }

    /// <summary>Expands RRULEs (incl. EXDATE and RECURRENCE-ID overrides, handled by Ical.Net)
    /// and converts to local time. VTIMEZONE data in the feed drives the conversion.</summary>
    private static void AddEvents(List<EventDto> events, Calendar cal, int feedIndex, DateTime windowStart, DateTime windowEnd)
    {
        foreach (var occurrence in cal.GetOccurrences(windowStart, windowEnd))
        {
            if (occurrence.Source is not CalendarEvent ev) continue;

            var title = string.IsNullOrWhiteSpace(ev.Summary) ? "(untitled)" : ev.Summary.Trim();

            if (ev.IsAllDay)
            {
                // Date-only values: never route through UTC (that could shift the date).
                // DTEND on all-day events is exclusive; convert to an inclusive display date.
                var s = occurrence.Period.StartTime.Value.Date;
                var endRaw = occurrence.Period.EndTime?.Value.Date ?? s;
                var e = endRaw > s ? endRaw.AddDays(-1) : s;
                events.Add(new EventDto(feedIndex, title, true, s.ToString("yyyy-MM-dd"), e.ToString("yyyy-MM-dd")));
            }
            else
            {
                var start = ToLocal(occurrence.Period.StartTime);
                var end = occurrence.Period.EndTime is { } pe ? ToLocal(pe) : start;
                events.Add(new EventDto(feedIndex, title, false,
                    start.ToString("yyyy-MM-ddTHH:mm:ss"), end.ToString("yyyy-MM-ddTHH:mm:ss")));
            }
        }
    }

    /// <summary>Converts an ICS date-time (with VTIMEZONE/TZID or UTC) to machine-local time.
    /// Ical.Net 4.x's AsSystemLocal does not apply the TZID, so go through AsUtc explicitly.</summary>
    internal static DateTime ToLocal(Ical.Net.DataTypes.IDateTime value)
    {
        if (!value.HasTime) return value.Value.Date;
        if (string.IsNullOrEmpty(value.TzId) && !value.IsUtc) return value.Value; // floating: already local
        return value.AsUtc.ToLocalTime();
    }

    /// <summary>Validates a feed URL with a live fetch. Returns (ok, message) — e.g. "12 events".</summary>
    public static async Task<(bool Ok, string Message)> TestFeedAsync(string url)
    {
        if (IsGoogleShareLink(url))
            return (false, "this is a Google share link, not an ICS feed — in Google Calendar, right-click " +
                           "the calendar in the left sidebar → “Settings and sharing”, scroll to the bottom, " +
                           "and copy the “Secret address in iCal format” (ends in basic.ics)");
        try
        {
            var text = await FetchTextAsync(url);
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                return (false, "not an ICS file");
            var eventCount = CountOf(text, "BEGIN:VEVENT");
            var todoCount = CountOf(text, "BEGIN:VTODO");
            var msg = $"{eventCount} events" + (todoCount > 0 ? $", {todoCount} tasks" : "");
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, Shorten(ex));
        }
    }

    /// <summary>Recognizes Google Calendar share/subscribe links (e.g. …/calendar/u/0?cid=…),
    /// which return an HTML page rather than ICS data.</summary>
    private static bool IsGoogleShareLink(string url)
    {
        var u = url.Trim();
        return u.Contains("calendar.google.com", StringComparison.OrdinalIgnoreCase)
            && !u.Contains("/ical/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> FetchTextAsync(string url)
    {
        var u = url.Trim();
        if (u.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            u = "https://" + u["webcal://".Length..];
        if (u.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return await File.ReadAllTextAsync(new Uri(u).LocalPath);
        if (File.Exists(u)) // plain local path — handy for testing
            return await File.ReadAllTextAsync(u);

        using var response = await Http.GetAsync(u);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..16];
        return Path.Combine(AppPaths.CacheDir, $"feed_{hash}.ics");
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string Shorten(Exception ex)
    {
        var msg = (ex.InnerException ?? ex).Message;
        return msg.Length > 120 ? msg[..120] + "…" : msg;
    }
}
