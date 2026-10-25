using System.Text.Json;

namespace VideoPathTool.Core;

/// <summary>
/// Everything needed to resume editing one route.
///
/// The exported JSON only carries game coordinates, so it cannot answer
/// "which moment of the video is this?". The project file keeps the user's
/// annotations in video time, which is the only thing that can be drawn on the
/// timeline and only thing that survives a re-record.
/// </summary>
public sealed class RouteProject
{
    public string RouteName { get; set; } = string.Empty;
    public string VideoPath { get; set; } = string.Empty;
    public double VideoDurationSec { get; set; }

    /// <summary>The deliverable this project exports to (for display).</summary>
    public string RouteJson { get; set; } = string.Empty;

    /// <summary>Recorded window, so reopening shows the same range.</summary>
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    public List<GlideSegment> Glides { get; set; } = new();
    public List<PointAction> Actions { get; set; } = new();

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public RouteProject Clone() => new()
    {
        RouteName = RouteName,
        VideoPath = VideoPath,
        VideoDurationSec = VideoDurationSec,
        RouteJson = RouteJson,
        StartSec = StartSec,
        EndSec = EndSec,
        Glides = Glides.Select(g => new GlideSegment
        {
            StartSec = g.StartSec,
            EndSec = g.EndSec,
            Confidence = g.Confidence,
            Reason = g.Reason
        }).ToList(),
        Actions = Actions.ToList(),
        UpdatedUtc = UpdatedUtc
    };
}

/// <summary>
/// Reads and writes <see cref="RouteProject"/>.
///
/// Also knows how to pick up the two sidecar files written by the previous
/// version (<c>&lt;name&gt;_actions.json</c> / <c>&lt;name&gt;_glides.json</c>),
/// so upgrading does not silently drop annotations the user already made.
/// </summary>
public static class RouteProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Defaults are meaningful (time 0 is the first frame, offset 0 is "no
        // correction"), so they must survive the round trip.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Save(string path, RouteProject project)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        project.UpdatedUtc = DateTime.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(project, Options), new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// Loads the project for a route, migrating the old sidecars when the
    /// project file does not exist yet. Returns an empty project when nothing is
    /// found, so callers never have to null-check.
    /// </summary>
    public static RouteProject LoadOrMigrate(
        string projectPath,
        string routeName,
        string? routeRoot = null,
        string? legacyDebugDir = null)
    {
        var project = TryLoad(projectPath);
        if (project is not null)
        {
            return project;
        }

        project = new RouteProject { RouteName = routeName };

        // Same folder, previous naming scheme.
        var folder = AppPaths.RouteFolderFor(routeName, routeRoot);
        var glides = ReadList<GlideSegment>(Path.Combine(folder, "glides.json"));
        var actions = ReadList<PointAction>(Path.Combine(folder, "actions.json"));

        // Pre-folder layout.
        if (legacyDebugDir is not null)
        {
            glides ??= ReadList<GlideSegment>(Path.Combine(legacyDebugDir, routeName + "_glides.json"));
            actions ??= ReadList<PointAction>(Path.Combine(legacyDebugDir, routeName + "_actions.json"));
        }

        project.Glides = glides ?? new List<GlideSegment>();
        project.Actions = actions ?? new List<PointAction>();
        return project;
    }

    private static RouteProject? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<RouteProject>(File.ReadAllText(path), Options);
        }
        catch
        {
            // A corrupt project must not stop the tool from opening.
            return null;
        }
    }

    private static List<T>? ReadList<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options);
        }
        catch
        {
            return null;
        }
    }
}
