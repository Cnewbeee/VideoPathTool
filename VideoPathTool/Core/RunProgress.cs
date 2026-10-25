namespace VideoPathTool.Core;

/// <summary>Which stage of the pipeline is running.</summary>
public enum PipelineStage
{
    Idle,
    Loading,
    Recording,
    ScanningGlides,
    Exporting,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// One progress report from the map-matching pass.
///
/// The console frontend only needs the formatted string it already logs, but a
/// UI needs the numbers separately so it can draw a bar and an ETA.
/// </summary>
public readonly record struct RecordTick(
    double TimeSec,
    double TotalSec,
    int SampledFrames,
    int MatchedFrames,
    int FailedFrames,
    int InvalidFrames,
    int AcceptedPoints,
    double ElapsedSec)
{
    public double Fraction => TotalSec > 0 ? Math.Clamp(TimeSec / TotalSec, 0, 1) : 0;

    public double MatchRate => SampledFrames > 0 ? (double)MatchedFrames / SampledFrames : 0;

    /// <summary>Estimated remaining seconds, from the pace observed so far.</summary>
    public double EtaSec
    {
        get
        {
            if (TimeSec <= 0.5 || ElapsedSec <= 0.05)
            {
                return 0;
            }
            var secPerVideoSec = ElapsedSec / TimeSec;
            var remaining = Math.Max(0, TotalSec - TimeSec);
            return remaining * secPerVideoSec;
        }
    }
}

/// <summary>One progress report from the glide-scan pass.</summary>
public readonly record struct ScanTick(
    double TimeSec,
    double TotalSec,
    int DetectedFrames,
    int Windows,
    double ElapsedSec)
{
    public double Fraction => TotalSec > 0 ? Math.Clamp(TimeSec / TotalSec, 0, 1) : 0;

    public double EtaSec
    {
        get
        {
            if (TimeSec <= 0.5 || ElapsedSec <= 0.05)
            {
                return 0;
            }
            return Math.Max(0, TotalSec - TimeSec) * (ElapsedSec / TimeSec);
        }
    }
}

/// <summary>
/// Everything one run produced, kept so the waypoints can be rebuilt without
/// re-reading the video -- that is what makes "adjust and re-export" fast.
/// </summary>
public sealed class PipelineResult
{
    public List<RawPoint> Points { get; set; } = new();
    public List<RawPoint> AllSamples { get; set; } = new();
    public RecordStats Stats { get; set; } = new();

    /// <summary>Glide windows as detected; the UI may edit these before export.</summary>
    public List<GlideSegment> Glides { get; set; } = new();

    /// <summary>
    /// Manual operations bound to moments in the video. Also editable before
    /// export, exactly like the glide windows.
    /// </summary>
    public List<PointAction> Actions { get; set; } = new();

    /// <summary>How each action mapped onto the waypoint list (for reporting).</summary>
    public List<PointActionBinding> ActionBindings { get; set; } = new();

    public List<SegmentBreak> Breaks { get; set; } = new();
    public List<ProcessedPoint> Waypoints { get; set; } = new();

    /// <summary>Video length in seconds, for progress and ETA.</summary>
    public double TotalSec { get; set; }

    public string OutputPath { get; set; } = string.Empty;
    public string DebugCsvPath { get; set; } = string.Empty;

    public double MatchRate => Stats.SampledFrames > 0
        ? (double)Stats.MatchedFrames / Stats.SampledFrames
        : 0;
}
