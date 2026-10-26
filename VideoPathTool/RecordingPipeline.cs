using System.Diagnostics;
using System.Globalization;
using System.Text;
using VideoPathTool.Core;

namespace VideoPathTool;

/// <summary>
/// The whole video-to-route pipeline, detached from any frontend.
///
/// Previously this logic lived inside the console menu, which meant a UI could
/// only drive it by faking keystrokes. It is now a class so the console, the
/// WPF window and any future frontend share exactly one implementation.
///
/// The class is stateful on purpose: <see cref="Record"/> keeps the raw samples
/// in a <see cref="PipelineResult"/>, and <see cref="BuildWaypoints"/> /
/// <see cref="SaveJson"/> can be called again on that result any number of
/// times. That is what makes "tweak the glide windows, re-export" take
/// milliseconds instead of re-reading an 800 second video.
/// </summary>
public sealed class RecordingPipeline : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Action<string> _log;
    private TeyvatMapMatcher? _matcher;
    private SiftMapMatcher? _sift;
    private string _loadedAssetsDir = string.Empty;
    private bool _disposed;

    public RecordingPipeline(AppConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    /// <summary>
    /// Marker observations from the last heuristic glide scan. Empty when the
    /// YOLO detector was used. Kept so the side-car CSV can still be written.
    /// </summary>
    public List<GlideMarkerObservation> LastMarkerObservations { get; private set; } = new();

    /// <summary>The map layers, loaded once and reused across runs.</summary>
    public TeyvatMapMatcher GetMatcher()
    {
        if (_matcher != null && _loadedAssetsDir == _cfg.MapAssetsDir)
        {
            return _matcher;
        }
        if (!Directory.Exists(_cfg.MapAssetsDir))
        {
            throw new DirectoryNotFoundException($"地图资源目录不存在: {_cfg.MapAssetsDir}");
        }
        _log("正在加载地图层，请稍候...");
        var sw = Stopwatch.StartNew();
        _matcher?.Dispose();
        _matcher = new TeyvatMapMatcher();
        var count = _matcher.LoadLayers(_cfg.MapAssetsDir);
        _log($"地图层加载完成: {count} 层，用时 {sw.Elapsed.TotalSeconds:F1}s");

        _sift?.Dispose();
        _sift = null;
        if (_cfg.UseSift)
        {
            var kpPath = Path.Combine(_cfg.MapAssetsDir, _cfg.SiftFilePrefix + ".kp.bin");
            var descPath = Path.Combine(_cfg.MapAssetsDir, _cfg.SiftFilePrefix + ".mat.png");
            if (File.Exists(kpPath) && File.Exists(descPath))
            {
                _log("正在加载 SIFT 特征并建立索引...");
                var sw2 = Stopwatch.StartNew();
                _sift = new SiftMapMatcher();
                _sift.Load(kpPath, descPath);
                _log($"SIFT 加载完成: 特征点 {_sift.TrainKeyPointCount}，用时 {sw2.Elapsed.TotalSeconds:F1}s");
            }
            else
            {
                _log($"未找到 SIFT 特征文件，将仅使用模板匹配: {kpPath}");
            }
        }

        _loadedAssetsDir = _cfg.MapAssetsDir;
        return _matcher;
    }

    /// <summary>Builds a recorder bound to the current map layers.</summary>
    public Recorder CreateRecorder()
    {
        var matcher = GetMatcher();
        return new Recorder(_cfg, matcher, _sift, _log);
    }

    /// <summary>Length of the video in seconds, for progress and ETA.</summary>
    public static double ProbeVideoLength(string path)
    {
        using var cap = new OpenCvSharp.VideoCapture(path);
        if (!cap.IsOpened())
        {
            return 0;
        }
        var fps = cap.Get(OpenCvSharp.VideoCaptureProperties.Fps);
        var frames = cap.Get(OpenCvSharp.VideoCaptureProperties.FrameCount);
        return fps > 0 ? frames / fps : 0;
    }

    /// <summary>
    /// Pass 1: walks the video and records the position of every sampled frame.
    /// </summary>
    public PipelineResult Record(
        MinimapCircle circle,
        PauseToken? pause,
        CancellationToken ct,
        IProgress<RecordTick>? progress = null,
        Func<MinimapCircle>? liveCircle = null)
    {
        var recorder = CreateRecorder();
        var fullLength = ProbeVideoLength(_cfg.VideoPath);
        // Report the span that is actually being recorded, not the whole file:
        // a 0-30 s test on a 396 s video must not claim 396 s.
        var spanStart = Math.Max(0, _cfg.StartTimeSec);
        var spanEnd = _cfg.EndTimeSec > 0 ? Math.Min(fullLength, _cfg.EndTimeSec) : fullLength;
        var result = new PipelineResult
        {
            TotalSec = spanEnd > spanStart ? spanEnd : fullLength,
            OutputPath = _cfg.OutputPath,
            DebugCsvPath = _cfg.DebugCsvPath
        };

        var (points, allSamples) = recorder.Run(circle, ct, out var stats, pause, progress, liveCircle);
        result.Points = points;
        result.AllSamples = allSamples;
        result.Stats = stats;
        return result;
    }

    /// <summary>
    /// Pass 2: finds the glide windows.
    ///
    /// Uses the trained YOLO detector when the model is present, otherwise the
    /// minimap marker heuristic. A cancelled or paused run returns whatever was
    /// found so far rather than throwing.
    /// </summary>
    public List<GlideSegment> ScanGlides(
        MinimapCircle circle,
        PauseToken? pause,
        CancellationToken ct,
        IProgress<ScanTick>? progress = null)
    {
        var result = new List<GlideSegment>();
        LastMarkerObservations = new List<GlideMarkerObservation>();
        if (!_cfg.DetectGlide)
        {
            return result;
        }

        if (_cfg.GlideUseYolo && TryLoadGlideModel(out var yolo))
        {
            using (yolo)
            {
                var recorder = CreateRecorder();
                var found = recorder.ScanForGlides(
                    yolo, _cfg.GlideYoloSampleFps, _log, ct, pause, progress);
                result.AddRange(found);
                _log($"YOLO 检出 {found.Count} 段滑翔窗口");
            }
            return result;
        }

        // Fallback: the minimap marker heuristic. The wing glyph flaps, so the
        // marker has to be watched at a much higher rate than the matching.
        _log("使用小地图标记启发式检测滑翔（精度低于 YOLO）");
        var fallback = CreateRecorder();
        var obs = fallback.SampleMarkers(circle, _cfg.GlideMarkerSampleFps, ct);
        LastMarkerObservations = obs;
        _log($"滑翔标记采样完成，共 {obs.Count} 帧");
        result.AddRange(DetectGlidesHeuristic(obs, new List<GlideSegment>(), _cfg));
        return result;
    }

    /// <summary>Heuristic glide detector over minimap marker observations.</summary>
    public static List<GlideSegment> DetectGlidesHeuristic(
        List<GlideMarkerObservation> observations,
        List<GlideSegment> existing,
        AppConfig cfg)
    {
        if (observations.Count < 8)
        {
            return existing;
        }
        var detector = new GlideDetector
        {
            MinAspectRatio = cfg.GlideMinAspect,
            MinMarkerMotion = cfg.GlideMinMotion,
            MergeGapSec = cfg.GlideMergeGapSec,
            MinDurationSec = cfg.GlideMinDurationSec
        };
        var detected = detector.Detect(observations);
        existing.AddRange(detected);
        return existing;
    }

    /// <summary>
    /// Loads the trained wind-wing detector, if the model file can be found.
    /// Looks next to the executable first, then in the console tool's output.
    /// </summary>
    public bool TryLoadGlideModel(out GlideYoloDetector detector)
    {
        detector = null!;
        var path = ResolveGlideModelPath();
        if (path is null)
        {
            _log($"未找到滑翔检测模型: {_cfg.GlideModelPath}");
            _log("  将回退到小地图启发式检测（精度较低）。");
            return false;
        }
        try
        {
            detector = new GlideYoloDetector(path) { ScoreThreshold = (float)_cfg.GlideYoloScore };
            detector.InputResolution = _cfg.GlideYoloInputSize;
            _log($"滑翔检测模型: {path}  输入分辨率 {detector.InputResolution}");
            return true;
        }
        catch (Exception ex)
        {
            _log($"滑翔检测模型加载失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>Resolves the ONNX model path, searching a few plausible places.</summary>
    public string? ResolveGlideModelPath()
    {
        var configured = _cfg.GlideModelPath;
        if (Path.IsPathRooted(configured))
        {
            return File.Exists(configured) ? configured : null;
        }
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VideoPathTool", configured),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "VideoPathTool", "bin", "Debug", "net8.0", configured)
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    /// <summary>
    /// Turns the recorded samples into BGI waypoints and marks the fly legs.
    /// Both the glide windows and the point actions are parameters because the
    /// UI lets the user correct them before exporting.
    /// </summary>
    public List<ProcessedPoint> BuildWaypoints(
        PipelineResult result,
        IReadOnlyList<GlideSegment>? glideWindows = null,
        IReadOnlyList<PointAction>? pointActions = null)
    {
        var windows = glideWindows?.ToList() ?? result.Glides.ToList();
        var actions = pointActions?.ToList() ?? result.Actions;
        var waypoints = PathPostProcessor.BuildWaypoints(
            result.Points, _cfg, out var breaks, out _);
        result.Breaks = breaks;
        PathPostProcessor.ApplyGlideModes(waypoints, windows, _cfg.MoveMode, _cfg.GlideWindowPadSec);
        result.ActionBindings = PathPostProcessor.ApplyPointActions(waypoints, actions);
        result.Waypoints = waypoints;
        result.Actions = actions;
        return waypoints;
    }

    /// <summary>Writes the BGI pathing JSON.</summary>
    public void SaveJson(PipelineResult result, string? outputPath = null, bool updateResult = true)
    {
        var path = string.IsNullOrWhiteSpace(outputPath) ? _cfg.OutputPath : outputPath;
        var name = string.IsNullOrWhiteSpace(_cfg.RouteName)
            ? Path.GetFileNameWithoutExtension(_cfg.VideoPath)
            : _cfg.RouteName;

        var task = new PathingTaskOutput
        {
            Info = new PathingTaskInfoOutput
            {
                Name = name,
                Description = $"由视频 {Path.GetFileName(_cfg.VideoPath)} 自动生成，" +
                              $"原始匹配点 {result.Points.Count} 个，导出路径点 {result.Waypoints.Count} 个",
                MapName = _cfg.MapName,
                MapMatchMethod = _cfg.MapMatchMethod
            }
        };
        for (var i = 0; i < result.Waypoints.Count; i++)
        {
            var wp = result.Waypoints[i];
            task.Positions.Add(new WaypointOutput
            {
                Id = i + 1,
                X = wp.X,
                Y = wp.Y,
                Type = wp.Type,
                MoveMode = wp.MoveMode,
                Action = string.IsNullOrWhiteSpace(wp.Action) ? null : wp.Action,
                ActionParams = string.IsNullOrWhiteSpace(wp.ActionParams) ? null : wp.ActionParams
            });
        }
        task.Save(path);
        // An export to a chosen folder is a copy: the project's canonical path
        // must keep pointing at its own route folder, or the next 重新导出 would
        // silently write into the copy location.
        if (updateResult)
        {
            result.OutputPath = path;
        }
        _log($"已导出 JSON: {path}  (路径点 {result.Waypoints.Count} 个)");
    }

    /// <summary>Writes the per-sample debug CSV (re-runnable without the video).</summary>
    public void SaveCsv(PipelineResult result, string? csvPath = null)
    {
        var path = string.IsNullOrWhiteSpace(csvPath) ? _cfg.DebugCsvPath : csvPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // O(1) membership: RawPoint is a record, so a HashSet uses value equality.
        var accepted = new HashSet<RawPoint>(result.Points);
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("time_sec,x,y,confidence,accepted,fresh");
            foreach (var p in result.AllSamples)
            {
                writer.WriteLine(FormattableString.Invariant(
                    $"{p.TimeSec:F2},{p.X:F2},{p.Y:F2},{p.Confidence:F4},{(accepted.Contains(p) ? "True" : "False")},{p.Fresh}"));
            }
        }
        result.DebugCsvPath = path;
        _log($"已导出调试 CSV: {path}");
    }

    /// <summary>Writes the minimap marker observations used by the heuristic.</summary>
    public void SaveMarkerCsv(IReadOnlyList<GlideMarkerObservation> observations, string? csvPath = null)
    {
        if (observations.Count == 0)
        {
            return;
        }
        var basePath = string.IsNullOrWhiteSpace(csvPath) ? _cfg.DebugCsvPath : csvPath;
        var markerCsv = Path.Combine(
            Path.GetDirectoryName(basePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(basePath) + "_markers.csv");
        using var w = new StreamWriter(markerCsv, false, new UTF8Encoding(false));
        w.WriteLine("time_sec,aspect,marker_x,marker_y,dist,area,valid");
        foreach (var o in observations)
        {
            w.WriteLine(FormattableString.Invariant(
                $"{o.TimeSec:F2},{o.AspectRatio:F4},{o.CenterX:F2},{o.CenterY:F2},{o.DistanceFromCenter:F2},{o.Area:F0},{o.Valid}"));
        }
        _log($"已导出滑翔标记 CSV: {markerCsv}");
    }

    /// <summary>
    /// Re-reads a debug CSV into a result, so the waypoints can be rebuilt
    /// without touching the video at all.
    /// </summary>
    public static (PipelineResult Result, int AllRows) LoadFromCsv(string csvPath, AppConfig cfg)
    {
        var result = new PipelineResult { DebugCsvPath = csvPath, OutputPath = cfg.OutputPath };
        var accepted = new List<RawPoint>();
        var all = new List<RawPoint>();
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 5)
            {
                continue;
            }
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                continue;
            }
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf);
            var fresh = parts.Length <= 5 || (bool.TryParse(parts[5], out var fr) && fr);
            var sample = new RawPoint(t, x, y, conf) { Fresh = fresh };
            all.Add(sample);
            if (bool.TryParse(parts[4], out var ok) && ok)
            {
                accepted.Add(sample);
            }
        }
        result.Points = accepted;
        result.AllSamples = all;
        result.Stats = new RecordStats { SampledFrames = all.Count, MatchedFrames = accepted.Count };
        return (result, all.Count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _matcher?.Dispose();
        _sift?.Dispose();
    }
}
