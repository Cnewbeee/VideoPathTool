using System.Text.Json;
using System.Text.Json.Serialization;
using VideoPathTool.Core;

namespace VideoPathTool;

public class AppConfig
{
    /// <summary>Empty means "not chosen yet"; paste a link or pick a file.</summary>
    public string VideoPath { get; set; } = "";

    /// <summary>
    /// Fixed folder that downloaded videos land in. Kept separate from the
    /// video path so "give a link" always writes to the same place.
    /// </summary>
    public string DownloadDir { get; set; } = AppPaths.DownloadDir;

    /// <summary>yt-dlp executable. Empty means "search the usual places".</summary>
    public string YtDlpPath { get; set; } = AppPaths.YtDlpPath;

    /// <summary>ffmpeg executable, used to merge audio and to build preview proxies.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// When a fresh <c>&lt;name&gt;.proxy.mp4</c> exists next to the video, the
    /// editor previews that instead. Recording always reads the original.
    /// </summary>
    public bool UsePreviewProxy { get; set; } = true;

    /// <summary>Root that holds one folder per route (see <see cref="AppPaths"/>).</summary>
    public string RouteDir { get; set; } = AppPaths.RouteDir;

    /// <summary>
    /// Retained only so old config files keep loading. Each route now has its
    /// own folder, so debug output is no longer a separate tree.
    /// </summary>
    public string DebugDir { get; set; } = AppPaths.LegacyDebugDir;

    /// <summary>
    /// Extracted Teyvat map assets (the <c>...\Assets\Map\Teyvat</c> folder from
    /// the game). Not redistributed with this tool: point it at your own copy.
    /// </summary>
    public string MapAssetsDir { get; set; } =
        Path.Combine(AppPaths.BaseDir, "assets", "map", "contentFiles", "any", "any", "Assets", "Map", "Teyvat");
    public bool AutoDetectMinimap { get; set; } = true;
    public int MinimapCenterX { get; set; } = 280;
    public int MinimapCenterY { get; set; } = 268;
    public int MinimapRadius { get; set; } = 289;
    public int DetectSearchSize { get; set; } = 650;
    public double CropRatio { get; set; } = 1.0;
    public double SampleFps { get; set; } = 2.0;
    public double ConfidenceThreshold { get; set; } = 0.95;
    public int MatchRank { get; set; } = 2;
    /// <summary>Retained only so old config files keep loading. The pipeline no
    /// longer decimates before simplifying: doing that destroyed curve detail
    /// before the simplifier ever saw it.</summary>
    public double WaypointMinDistance { get; set; } = 8.0;

    /// <summary>Retained as a migration fallback for <see cref="SagittaTolerance"/>.</summary>
    public double RdpEpsilon { get; set; } = 3.0;

    /// <summary>
    /// Maximum distance, in game units, that the simplified polyline may stray
    /// from the actually recorded path. This is a hard guarantee, not a hint.
    /// BGI itself tolerates about 4 units of arrival slop, so 1.5 keeps the
    /// emulated route well inside the noise floor while still cutting the
    /// waypoint count by roughly a third. Lower it for tight obstacle corridors.
    /// </summary>
    public double SagittaTolerance { get; set; } = 1.5;

    /// <summary>
    /// Longest straight run allowed between two waypoints. Capped so a long
    /// straight cannot drift across a corner, but kept above 20 because BGI
    /// only sprints when the distance to the waypoint exceeds 20 units
    /// (PathExecutor.MoveTo). Shorter values disable sprinting and make the
    /// route slower.
    /// </summary>
    public double MaxChordLength { get; set; } = 40.0;

    /// <summary>
    /// Turns sharper than this are never merged away, whatever the tolerance
    /// says, so a short but real corner is not swallowed by its neighbours.
    /// </summary>
    public double CornerGuardDeg { get; set; } = 25.0;

    /// <summary>
    /// A single sample step longer than this cannot have been walked. Tracking
    /// is sampled at a fixed rate, so at the observed p99 speed a real step
    /// stays near 11 units. Larger gaps are treated as lost tracking: the route
    /// is split instead of drawing a straight leg that was never traversed.
    /// </summary>
    public double MaxBridgeDistance { get; set; } = 20.0;

    /// <summary>
    /// Segments whose total walked length is below this are discarded. They are
    /// fragments around a tracking dropout: too short to carry a usable
    /// direction, and each one would otherwise cost a spurious teleport because
    /// BGI starts every continuous segment with one.
    /// </summary>
    public double MinSegmentArcLength { get; set; } = 30.0;

    /// <summary>
    /// Detect gliding legs and mark those waypoints with move_mode "fly" so BGI
    /// opens the glider for them.
    ///
    /// The detector reads the minimap: while gliding, the minimap draws the
    /// wind-wing glyph, which is far more elongated than the normal marker.
    /// The world map screen also has a very elongated cyan marker, so the
    /// detector additionally requires the marker to MOVE, which the static
    /// world map never does.
    ///
    /// Measured on p5.mp4: F1 0.99 against hand-labelled glides (random
    /// baseline 0.15). The reported windows include a little of the standing
    /// time before takeoff and after landing, so review the marked waypoints.
    /// </summary>
    public bool DetectGlide { get; set; } = false;

    /// <summary>Minimum length/width ratio of the minimap marker to count as a wing.</summary>
    public double GlideMinAspect { get; set; } = 6.5;

    /// <summary>Minimum centroid movement of the minimap marker, in pixels per sample.</summary>
    public double GlideMinMotion { get; set; } = 0.1;

    /// <summary>Merge glide windows separated by less than this, in seconds.</summary>
    public double GlideMergeGapSec { get; set; } = 1.0;

    /// <summary>Minimum duration of a glide window, in seconds.</summary>
    public double GlideMinDurationSec { get; set; } = 2.0;

    /// <summary>
    /// Sampling rate for the minimap marker, in frames per second. This is
    /// separate from the map matching rate because the wing glyph flaps: at
    /// the 2 fps used for matching, F1 drops from 0.99 to 0.74. The marker
    /// pass is cheap (no template matching), so 10 fps costs little.
    /// </summary>
    public int GlideMarkerSampleFps { get; set; } = 10;

    /// <summary>
    /// Path to the trained YOLO model (ONNX) used to detect the deployed
    /// wind-wing. When the file exists it is used; otherwise the tool falls
    /// back to the minimap marker heuristic.
    /// </summary>
    public string GlideModelPath { get; set; } = "glider.onnx";

    /// <summary>
    /// Use the YOLO model instead of the minimap marker heuristic.
    ///
    /// The learned detector is much more accurate: on p5.mp4 + p3.mp4 it
    /// found every confirmed glide segment (11 of 11) with no false
    /// positives, while the heuristic reached F1 0.75 with a precision of
    /// 0.64. The reason is that the game's map draws the wing, the flight
    /// trail and the character's CAPE in the same cyan, and the cape in
    /// particular looks exactly like a deployed wing while running.
    /// </summary>
    public bool GlideUseYolo { get; set; } = true;

    /// <summary>Confidence threshold for the YOLO glide detector.</summary>
    public double GlideYoloScore { get; set; } = 0.30;

    /// <summary>
    /// Sampling rate for the YOLO glide detector, in frames per second. The
    /// video decode dominates the cost, so 3 fps is a good trade: a glide
    /// lasts seconds, so its boundaries are resolved to about 0.3 s.
    /// </summary>
    public int GlideYoloSampleFps { get; set; } = 3;

    /// <summary>Batch size for the YOLO detector.</summary>
    public int GlideYoloBatch { get; set; } = 8;

    /// <summary>
    /// Side length of the square input the YOLO detector is fed.
    /// The model is exported with a dynamic input size. Measured over the
    /// whole of p5 and p3:
    ///   320 -> 9.6 ms/frame, 17/27 glide segments
    ///   416 -> 18.7 ms/frame, 20/27
    ///   480 -> 23.6 ms/frame, 20/27   (default, best balance)
    ///   640 -> 50.0 ms/frame, 20/27   (the training resolution, slowest)
    /// </summary>
    public int GlideYoloInputSize { get; set; } = 480;

    /// <summary>
    /// How far the detected glide window is widened on both sides, in
    /// seconds. The detector only fires while the wing is fully deployed, but
    /// the character is already airborne a moment earlier and stays airborne
    /// a moment longer. Walking is impossible there, so those waypoints
    /// belong to the glide leg too.
    /// </summary>
    public double GlideWindowPadSec { get; set; } = 1.0;

    public double TeleportJumpDistance { get; set; } = 300.0;
    public double CutGapSec { get; set; } = 5.0;
    public double CutJumpDistance { get; set; } = 100.0;
    public string MoveMode { get; set; } = "run";
    public string MapName { get; set; } = "Teyvat";
    public string MapMatchMethod { get; set; } = "TemplateMatch";
    public string RouteName { get; set; } = "";

    /// <summary>Derived from <see cref="RouteDir"/> + <see cref="RouteName"/>.</summary>
    public string OutputPath { get; set; } = "";

    /// <summary>Derived from <see cref="RouteDir"/> + <see cref="RouteName"/>.</summary>
    public string DebugCsvPath { get; set; } = "";

    /// <summary>Editor state for this route: glide windows and point actions.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Window layout, remembered so the panel does not pop back open.</summary>
    public bool SidebarCollapsed { get; set; }

    public double SidebarWidth { get; set; } = 392;

    /// <summary>Derived from <see cref="RouteDir"/> + <see cref="RouteName"/>.</summary>
    public string PreviewDir { get; set; } = "";
    public double PreviewEverySec { get; set; } = 60.0;
    public int MaxFailedPreviews { get; set; } = 30;
    public double MinMapGrayStd { get; set; } = 22.0;
    public double MinMapLaplacianVar { get; set; } = 40.0;
    public bool UseSift { get; set; } = true;
    public string SiftFilePrefix { get; set; } = "Teyvat_0_2048_SIFT";
    public int SiftMinInliers { get; set; } = 5;
    public int SiftStrongInliers { get; set; } = 10;
    public double MaxMoveSpeed { get; set; } = 10.0;
    public double MoveMargin { get; set; } = 15.0;
    public double ResyncGapSec { get; set; } = 3.0;
    public double ResyncConfidence { get; set; } = 0.97;
    public double ResyncWindowSec { get; set; } = 15.0;
    public int MinPlayerMarkerPixels { get; set; } = 250;
    public double MaxPlayerMarkerOffset { get; set; } = 20.0;
    public bool UsePlayerMarker { get; set; } = true;
    public int MarkerSearchRadius { get; set; } = 50;
    public double HoldSimilarity { get; set; } = 0.995;
    public double HoldMaxDistance { get; set; } = 15.0;
    public double StartTimeSec { get; set; } = 0.0;
    public double EndTimeSec { get; set; } = 0.0;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Recomputes every per-route path from the route root and the route name.
    /// Called whenever the video changes, so "give a link" needs no path typing
    /// at all, and guarantees one video -> one folder -> one JSON.
    /// </summary>
    public void ApplyDerivedPaths(string? routeName = null)
    {
        var stem = string.IsNullOrWhiteSpace(routeName)
            ? AppPaths.RouteNameFor(VideoPath)
            : AppPaths.SafeName(routeName);

        // Configs saved before the per-route folder layout point at the old
        // flat tree; move them to the new root instead of silently creating
        // route folders inside the legacy one.
        if (string.IsNullOrWhiteSpace(RouteDir) || SamePath(RouteDir, AppPaths.LegacyRouteDir))
        {
            RouteDir = AppPaths.RouteDir;
        }

        RouteName = stem;
        OutputPath = AppPaths.RouteJsonPathFor(stem, RouteDir);
        DebugCsvPath = AppPaths.DebugCsvPathFor(stem, RouteDir);
        ProjectPath = AppPaths.ProjectPathFor(stem, RouteDir);
        PreviewDir = AppPaths.PreviewDirFor(stem, RouteDir);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
