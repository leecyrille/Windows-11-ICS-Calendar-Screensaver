namespace CalendarSaver;

public class PhotoService
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    /// <summary>Recursively scans the configured folders. Missing/inaccessible folders are skipped
    /// silently — an empty result makes the page hide the photo panel.</summary>
    public List<PhotoDto> Scan(IReadOnlyList<string> folders)
    {
        var photos = new List<PhotoDto>();
        for (var i = 0; i < folders.Count; i++)
        {
            var root = folders[i];
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                };
                foreach (var file in Directory.EnumerateFiles(root, "*", options))
                {
                    if (!Extensions.Contains(Path.GetExtension(file))) continue;
                    photos.Add(new PhotoDto(i, Path.GetRelativePath(root, file)));
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Photo scan failed for '{root}': {ex.Message}");
            }
        }
        return photos;
    }
}
