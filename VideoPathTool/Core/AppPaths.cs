namespace VideoPathTool.Core;

/// <summary>
/// Where downloads and generated routes go.
///
/// Everything hangs off <see cref="BaseDir"/>, the folder the executable lives
/// in, so a fresh checkout works without editing any path. Anyone who wants the
/// data somewhere else sets the folders in the config file; the shipped defaults
/// assume the natural layout next to the program.
///
/// Layout (one folder per video, one JSON per video):
///   &lt;base&gt;\video\downloads\&lt;title&gt;.mp4       source video
///   &lt;base&gt;\video\routes\&lt;name&gt;\&lt;name&gt;.json    the deliverable
///   &lt;base&gt;\video\routes\&lt;name&gt;\debug.csv       recorded samples
///   &lt;base&gt;\video\routes\&lt;name&gt;\project.json   editor state
///   &lt;base&gt;\video\routes\&lt;name&gt;\previews\      match previews
///   &lt;base&gt;\video\routes\_archive\             superseded artefacts
///   &lt;base&gt;\tools\yt-dlp.exe                    download helper
/// </summary>
public static class AppPaths
{
    /// <summary>Folder the executable runs from; the root of all user data.</summary>
    public static string BaseDir { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string VideoRoot => Path.Combine(BaseDir, "video");

    /// <summary>Helper binaries (yt-dlp).</summary>
    public static string ToolsDir => Path.Combine(BaseDir, "tools");

    public static string YtDlpPath => Path.Combine(ToolsDir, "yt-dlp.exe");

    /// <summary>Downloaded videos.</summary>
    public static string DownloadDir => Path.Combine(VideoRoot, "downloads");

    /// <summary>Root that holds one folder per route.</summary>
    public static string RouteDir => Path.Combine(VideoRoot, "routes");

    /// <summary>Old flat locations, only used to migrate existing files.</summary>
    public static string LegacyRouteDir => Path.Combine(VideoRoot, "output", "routes");

    public static string LegacyDebugDir => Path.Combine(VideoRoot, "output", "debug");

    /// <summary>Where superseded artefacts are parked.</summary>
    public static string ArchiveDir => Path.Combine(RouteDir, "_archive");

    /// <summary>Strips characters Windows will not accept in a file name.</summary>
    public static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "route";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        var cleaned = sb.ToString().Trim().TrimEnd('.');
        return string.IsNullOrEmpty(cleaned) ? "route" : cleaned;
    }

    /// <summary>Route name derived from a video path: "p5.mp4" -> "p5".</summary>
    public static string RouteNameFor(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return "route";
        }
        return SafeName(Path.GetFileNameWithoutExtension(videoPath));
    }

    /// <summary>The folder that owns everything about one video.</summary>
    public static string RouteFolderFor(string routeName, string? routeRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(routeRoot) ? RouteDir : routeRoot;
        return Path.Combine(root, SafeName(routeName));
    }

    /// <summary>The single deliverable JSON for a route.</summary>
    public static string RouteJsonPathFor(string routeName, string? routeRoot = null)
        => Path.Combine(RouteFolderFor(routeName, routeRoot), SafeName(routeName) + ".json");

    /// <summary>Recorded samples, so the route can be rebuilt without the video.</summary>
    public static string DebugCsvPathFor(string routeName, string? routeRoot = null)
        => Path.Combine(RouteFolderFor(routeName, routeRoot), "debug.csv");

    /// <summary>Editor state: glide windows and point actions.</summary>
    public static string ProjectPathFor(string routeName, string? routeRoot = null)
        => Path.Combine(RouteFolderFor(routeName, routeRoot), "project.json");

    /// <summary>Preview frames written while matching.</summary>
    public static string PreviewDirFor(string routeName, string? routeRoot = null)
        => Path.Combine(RouteFolderFor(routeName, routeRoot), "previews");

    /// <summary>Per-sample screenshots: minimap crop and video frame.</summary>
    public static string ShotDirFor(string routeName, string? routeRoot = null)
        => Path.Combine(RouteFolderFor(routeName, routeRoot), "shots");

    // ---- video-path convenience overloads ----

    public static string RoutePathFor(string videoPath)
        => RouteJsonPathFor(RouteNameFor(videoPath));

    public static string DebugCsvPathForVideo(string videoPath)
        => DebugCsvPathFor(RouteNameFor(videoPath));

    public static string ProjectPathForVideo(string videoPath)
        => ProjectPathFor(RouteNameFor(videoPath));

    /// <summary>
    /// Recovers a route name from a folder or file path, so "load a CSV" and
    /// "load a JSON" can both point the editor at the right project.
    /// </summary>
    public static string RouteNameFromStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "route";
        }
        var name = Path.GetFileNameWithoutExtension(stem);
        if (string.Equals(name, "debug", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "project", StringComparison.OrdinalIgnoreCase))
        {
            // "<root>\<name>\debug.csv" -> "<name>"
            var parent = Path.GetDirectoryName(stem);
            if (!string.IsNullOrEmpty(parent))
            {
                name = Path.GetFileName(parent);
            }
        }
        else if (name.EndsWith("_debug", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"_debug".Length];
        }
        else if (name.EndsWith("_actions", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"_actions".Length];
        }
        else if (name.EndsWith("_glides", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"_glides".Length];
        }
        return SafeName(name);
    }
}
