# Spec: Calendar + Photo Slideshow Screensaver for Windows 11

## Goal

Build a Windows 11 screensaver (`.scr`) that displays a full-month calendar aggregated from multiple Google Calendar ICS perma-links, a task list, and a photo slideshow collage. Optimized for a single 32" monitor (assume 4K, but must work at 1440p). It should look genuinely beautiful — this is a wall calendar for the home/office, not a utility window.

## Tech stack

- .NET 8 WinForms host app, published as single-file exe, renamed to `.scr`
- **WebView2** for all rendering — the entire UI is one embedded HTML/JS/CSS page
- **Ical.Net** (NuGet) for ICS parsing and RRULE recurrence expansion
- No other runtime dependencies. WebView2 runtime is preinstalled on Windows 11.

## Architecture

C# host is responsible for: screensaver argument plumbing, fetching ICS feeds, scanning photo folders, reading/writing settings, and pushing data into the page. The web page is responsible for: all layout, rendering, text auto-scaling, slideshow animation, and detecting user input to exit.

Data flow: C# fetches/parses → serializes to JSON → `CoreWebView2.PostWebMessageAsJson()` → JS renders. JS never fetches anything over the network (avoids CORS on ICS hosts). Photos are served to the page via `SetVirtualHostNameToFolderMapping` (map each configured folder to a virtual host, e.g. `https://photos0/`, `https://photos1/`).

## Screensaver plumbing (get this right — it's fiddly)

- Parse first CLI arg case-insensitively, first two chars only: `/s` run, `/c` or `/c:<hwnd>` settings, `/p <hwnd>` preview (no-op, just exit), no args → settings.
- `/s`: borderless topmost form, `Cursor.Hide()`. Content on the **primary monitor only**; secondary monitors get plain black forms.
- **Input handling must be done in JS**, because WebView2 swallows keyboard/mouse from the WinForms host. In the page: listen for `keydown`, `mousedown`, and `mousemove` (mousemove only exits after cumulative movement > 10px from initial position — jittery mice must not kill it). Relay via `window.chrome.webview.postMessage(...)` → C# calls `Application.Exit()`.
- **WebView2 user data folder must be set explicitly** to `%LOCALAPPDATA%\CalendarSaver\WebView2` — the `.scr` runs from System32 which is not writable, and the default (next to the exe) will crash.
- Embed the HTML/CSS/JS as embedded resources; load via `NavigateToString` or virtual host mapping. Never read files from beside the `.scr`.

## Layout (32" monitor, landscape)

Left ~80% / right ~20% split:

1. **Month calendar** — most of the left region. Full current-month grid, 7 columns, week starts Monday. Header with month + year, large and elegant.
2. **Task list** — slim vertical panel between the calendar grid and the photo panel (~15% of the left region's width). If this crowds the calendar at 1440p, a bottom strip is the acceptable fallback — make it a constant that's easy to change.
3. **Photo slideshow** — right 1/5 of the screen, full height. A **collage** (2–4 photos tiled in a vertical masonry arrangement), not a single image.

## Calendar requirements

- **Month view only.** No week/day/agenda modes.
- Show **all events for every day** — never truncate to "+3 more". Day cells are equal-height; instead of truncating, **auto-scale event text** so the busiest day's events all fit. Implementation: render at a base font size, measure overflow, then binary-search a global scale factor (or per-cell with a floor) until everything fits. Re-run on data refresh and at midnight.
- Each feed gets a display name and color (from settings). Events render as color-coded chips: time (compact, e.g. `9:00`) + title. All-day events render as full-width bars at the top of the cell.
- Today's cell is prominently highlighted. Days from adjacent months are dimmed. Weekend columns subtly differentiated.
- Recurrence: expand RRULEs via `Ical.Net`'s `GetOccurrences()` over the visible month ± 1 week. Handle EXDATE and overridden instances (RECURRENCE-ID).
- Timezones: convert everything to local time. Google ICS feeds carry VTIMEZONE — Ical.Net handles this; verify with a feed containing events created in another timezone.
- Refresh feeds every 15 minutes (configurable). Cache last good response per feed on disk so one dead URL or offline start doesn't blank the calendar. Roll the grid over automatically at midnight on month change.

## Task list requirements

- Parse **VTODO** components from the same ICS feeds; render incomplete tasks sorted by due date (overdue highlighted, undated at bottom), color-coded by feed.
- **Known limitation to surface, not solve:** Google Calendar ICS exports do **not** include Google Tasks. VTODO support covers feeds that carry tasks (e.g. Nextcloud, Todoist ICS export). Add a code-level seam (an `ITaskSource` interface) so a Google Tasks API source can be added later without rework. Do not implement Google OAuth in v1.

## Photo slideshow requirements

- Settings define a list of **local folders**; scan recursively for jpg/jpeg/png/webp. Re-scan on each screensaver launch and every 30 minutes while running.
- Collage of 2–4 photos in the right panel, respecting aspect ratios (cover-fit tiles, masonry-style). Swap **one tile at a time** with a slow crossfade (~1.5s) — the whole panel should never blank at once.
- Photo switch interval is a **setting** (seconds; default 20). Random order, no repeats until the pool is exhausted.
- Downscale large images before display (browser-side via CSS is fine; if memory is an issue with big libraries, have C# serve resized thumbnails from a cache in `%LOCALAPPDATA%`).
- Empty/missing folders: the calendar expands to full width gracefully; no error UI.

## Settings dialog (`/c`)

WinForms dialog, persists JSON to `%APPDATA%\CalendarSaver\settings.json`:

```json
{
  "feeds": [{ "url": "", "name": "", "color": "#7aa2f7" }],
  "photoFolders": ["C:\\Users\\you\\Pictures\\Family"],
  "photoIntervalSeconds": 20,
  "refreshMinutes": 15
}
```

- Feeds: add/remove/edit rows — URL, display name, color picker. Preassign colors from a tasteful default palette.
- Photo folders: add (folder browser dialog) / remove.
- Numeric fields for photo interval and feed refresh.
- Validate ICS URLs on save with a test fetch; show inline pass/fail, but allow saving anyway.

## Visual design — "make it look really nice"

This matters as much as functionality. Direction:

- **Dark theme**, near-black background (`#0d1117`-ish) — OLED-friendly, screensaver-appropriate.
- Refined typography: a good variable font (embed **Inter**; do not depend on network font loading). Month header large and light-weight; generous whitespace; hairline cell borders (1px, ~10% white) rather than heavy grid lines.
- Event chips: rounded, feed color as a saturated left border or soft tinted background — not full-saturation blocks. Consistent 4px-scale spacing rhythm.
- Today: subtle glowing ring or accent-filled date badge.
- Motion: slow crossfades on photo swaps only. The calendar itself is static — no distracting animation.
- A thin footer/status line: last-refresh time and current clock (HH:mm), small and unobtrusive.
- Design for viewing from 2–3 meters: contrast and size floors matter more than density.

## Build, install, verify

- `dotnet publish -r win-x64 -p:PublishSingleFile=true --self-contained false`, rename output to `CalendarSaver.scr`.
- Dev loop: run the exe directly with `/s` / `/c` — no install needed. Enable WebView2 devtools in DEBUG builds only.
- Acceptance checks:
  1. `/s` runs fullscreen, exits on keypress and on real mouse movement, survives mouse jitter.
  2. Right-click → Install works; screensaver runs when launched by Windows from System32 (this specifically validates the user-data-folder and embedded-resource requirements).
  3. Two real Google Calendar ICS URLs render merged, color-coded, with a recurring event expanding correctly.
  4. A day with 8+ events shows all of them, readably, via auto-scaling.
  5. Slideshow cycles at the configured interval from two configured folders; removing all folders yields a full-width calendar.
  6. Settings dialog round-trips all values through `settings.json`.
  7. Feed URL unreachable → cached events still display, status line notes the stale feed.

## Open questions (decide during the session, defaults given)

- Task panel placement: side column (default) vs bottom strip.
- Show week numbers? Default no.
- Clock: footer only (default) vs large clock above the task panel.
