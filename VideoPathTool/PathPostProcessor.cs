namespace VideoPathTool;

using VideoPathTool.Core;

public record RawPoint(double TimeSec, double X, double Y, double Confidence)
{
    /// <summary>True when the minimap match actually produced this position.</summary>
    public bool Fresh { get; init; } = true;
}

public class ProcessedPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Type { get; set; } = "path";

    /// <summary>
    /// Time of the recorded sample this waypoint was derived from. Kept so
    /// that time-based annotations (glide windows) can be mapped onto the
    /// waypoints exactly, instead of being re-matched by coordinates.
    /// </summary>
    public double TimeSec { get; set; }

    /// <summary>
    /// Movement mode for this waypoint. "fly" marks a gliding leg, which makes
    /// BGI press space and hold the glider open
    /// (PathExecutor.MoveTo -> MoveModeEnum.Fly).
    /// </summary>
    public string MoveMode { get; set; } = "run";

    /// <summary>
    /// BGI action run on arrival at this waypoint (see
    /// <see cref="ActionCatalog"/>). Null means no action.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>Per-action parameter string, e.g. "8:00" for set_time.</summary>
    public string? ActionParams { get; set; }
}

/// <summary>
/// A place where the recorded trace was interrupted. Either a real teleport
/// (long jump consistent with a loading screen) or an "unverified" gap
/// (tracking was lost, so the straight leg between the two points was never
/// actually walked).
/// </summary>
public class SegmentBreak
{
    public double FromSec { get; set; }
    public double ToSec { get; set; }
    public double Distance { get; set; }
    public double ImpliedSpeed { get; set; }
    public string Kind { get; set; } = "unverified";
    public bool Emitted { get; set; } = true;
}

/// <summary>
/// Turns the raw per-sample map positions into BGI waypoints.
///
/// The old implementation ran <c>Resample(minDistance)</c> then <c>Rdp(epsilon)</c>.
/// That had two independent defects:
///
/// 1. RDP measures a point's distance to the CHORD. When the recorded path
///    detours around an obstacle and comes back, the return branch lies almost
///    exactly on the outbound chord, so its distance is ~0 and the whole
///    excursion is deleted. The emitted polyline then cuts straight through
///    terrain the player provably never traversed.
/// 2. Resampling ran BEFORE RDP at a fixed spacing, so RDP never saw the detail
///    it would have needed in order to protect it.
///
/// This version instead splits a chord until BOTH the perpendicular deviation
/// from the real samples AND the chord length are within limits. Every emitted
/// vertex is a real recorded sample, so no geometry is ever invented.
/// </summary>
public static class PathPostProcessor
{
    public const string KindTeleport = "teleport";
    public const string KindUnverified = "unverified";

    /// <summary>
    /// Effective deviation tolerance. <see cref="AppConfig.RdpEpsilon"/> is
    /// honoured as a migration fallback because it carried the same meaning
    /// (perpendicular distance) in the previous pipeline.
    /// </summary>
    public static double EffectiveTolerance(AppConfig cfg)
    {
        return cfg.SagittaTolerance > 0 ? cfg.SagittaTolerance : cfg.RdpEpsilon;
    }

    public static List<ProcessedPoint> BuildWaypoints(List<RawPoint> points, AppConfig cfg)
    {
        return BuildWaypoints(points, cfg, out _);
    }

    public static List<ProcessedPoint> BuildWaypoints(List<RawPoint> points, AppConfig cfg, out List<SegmentBreak> breaks)
    {
        return BuildWaypoints(points, cfg, out breaks, out _);
    }

    public static List<ProcessedPoint> BuildWaypoints(
        List<RawPoint> points,
        AppConfig cfg,
        out List<SegmentBreak> breaks,
        out List<GlideSegment> glides)
    {
        breaks = new List<SegmentBreak>();
        glides = new List<GlideSegment>();
        var result = new List<ProcessedPoint>();
        if (points.Count == 0)
        {
            return result;
        }

        var tol = EffectiveTolerance(cfg);
        var maxChord = Math.Max(tol, cfg.MaxChordLength);

        // Only a real teleport may be emitted as type "teleport". BGI reacts to
        // that type by opening the map and teleporting to the nearest anchor
        // (PathExecutor.HandleTeleportWaypoint), so tagging a segment that merely
        // lost tracking would make the run fly somewhere the recording never
        // teleported from. Those boundaries stay "path" and are simply walked
        // across, which is what actually happened in the video.
        foreach (var (segment, precededBy) in SplitSegments(points, cfg, breaks))
        {
            // A fragment shorter than one BGI arrival radius carries no usable
            // direction and would cost a spurious teleport, so drop it.
            if (segment.Count < 2 || SegmentArcLength(segment) < cfg.MinSegmentArcLength)
            {
                MarkDropped(breaks, segment);
                continue;
            }

            var startType = precededBy == KindTeleport ? KindTeleport : "path";
            var poly = Simplify(segment, tol, maxChord, cfg.CornerGuardDeg);
            for (var i = 0; i < poly.Count; i++)
            {
                result.Add(new ProcessedPoint
                {
                    X = Math.Round(poly[i].X, 2),
                    Y = Math.Round(poly[i].Y, 2),
                    TimeSec = poly[i].TimeSec,
                    Type = i == 0 ? startType : "path"
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the per-frame minimap marker observations the glide detector
    /// needs.
    /// </summary>
    public static List<GlideMarkerObservation> BuildGlideSamples(IReadOnlyList<GlideMarkerObservation> observations)
    {
        return new List<GlideMarkerObservation>(observations);
    }

    /// <summary>
    /// Marks the waypoints that lie inside a detected glide window with
    /// move_mode "fly", so BGI opens the glider for that leg.
    ///
    /// The mapping is exact: every simplified waypoint keeps the time of the
    /// recorded sample it came from, so a glide window in seconds maps onto
    /// waypoints without any coordinate re-matching.
    ///
    /// The window is widened by <paramref name="padSeconds"/> on both sides.
    /// The detector only fires while the wing is fully deployed, but the
    /// character is already airborne a moment earlier and stays airborne a
    /// moment longer -- walking is not possible there, so those waypoints
    /// belong to the glide leg too.
    /// </summary>
    public static void ApplyGlideModes(
        List<ProcessedPoint> waypoints,
        List<GlideSegment> glides,
        string baseMoveMode,
        double padSeconds = 0.0)
    {
        foreach (var wp in waypoints)
        {
            wp.MoveMode = baseMoveMode;
        }
        if (glides.Count == 0)
        {
            return;
        }
        foreach (var wp in waypoints)
        {
            foreach (var g in glides)
            {
                if (wp.TimeSec >= g.StartSec - padSeconds && wp.TimeSec <= g.EndSec + padSeconds)
                {
                    wp.MoveMode = "fly";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Maps manually placed point actions onto the nearest waypoint, by time.
    ///
    /// The user marks a moment in the video ("here, press the gadget") and the
    /// waypoint list changes every time the simplification settings do, so the
    /// binding has to be by timestamp rather than by index. <paramref name="maxSnapSec"/>
    /// bounds how far an action may travel: snapping a marker to a waypoint
    /// half a minute away would put the action in the wrong place, so that is
    /// reported as unbound instead of guessed.
    /// </summary>
    public static List<PointActionBinding> ApplyPointActions(
        List<ProcessedPoint> waypoints,
        IReadOnlyList<PointAction> actions,
        double maxSnapSec = 3.0)
    {
        var bindings = new List<PointActionBinding>();
        if (waypoints.Count == 0 || actions.Count == 0)
        {
            return bindings;
        }

        foreach (var a in actions)
        {
            if (!a.HasWork)
            {
                continue;
            }

            var best = -1;
            var bestDelta = double.MaxValue;
            for (var i = 0; i < waypoints.Count; i++)
            {
                var d = Math.Abs(waypoints[i].TimeSec - a.TimeSec);
                if (d < bestDelta)
                {
                    bestDelta = d;
                    best = i;
                }
            }

            var binding = new PointActionBinding { Source = a, DeltaSec = bestDelta };
            if (best < 0 || bestDelta > maxSnapSec)
            {
                bindings.Add(binding);
                continue;
            }

            var wp = waypoints[best];
            binding.WaypointIndex = best;
            binding.WaypointTimeSec = wp.TimeSec;

            if (Math.Abs(a.OffsetX) > 1e-9 || Math.Abs(a.OffsetY) > 1e-9)
            {
                wp.X = Math.Round(wp.X + a.OffsetX, 2);
                wp.Y = Math.Round(wp.Y + a.OffsetY, 2);
            }
            if (!string.IsNullOrWhiteSpace(a.WaypointType))
            {
                wp.Type = a.WaypointType;
            }
            if (!string.IsNullOrWhiteSpace(a.MoveMode))
            {
                wp.MoveMode = a.MoveMode;
            }
            if (!string.IsNullOrWhiteSpace(a.Action))
            {
                wp.Action = a.Action;
                wp.ActionParams = string.IsNullOrWhiteSpace(a.ActionParams) ? null : a.ActionParams.Trim();
            }

            bindings.Add(binding);
        }

        return bindings;
    }

    public static double Distance(RawPoint a, RawPoint b)
    {
        return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// <summary>Total walked length of a segment, ignoring dropout jumps.</summary>
    private static double SegmentArcLength(List<RawPoint> segment)
    {
        var total = 0.0;
        for (var i = 1; i < segment.Count; i++)
        {
            total += Distance(segment[i - 1], segment[i]);
        }
        return total;
    }

    // --------------------------------------------------------------- splitting

    private static void MarkDropped(List<SegmentBreak> breaks, List<RawPoint> segment)
    {
        if (segment.Count == 0)
        {
            return;
        }
        var first = segment[0];
        var last = segment[^1];
        breaks.Add(new SegmentBreak
        {
            FromSec = first.TimeSec,
            ToSec = last.TimeSec,
            Distance = segment.Count > 1 ? Distance(first, last) : 0,
            Kind = KindUnverified,
            Emitted = false
        });
    }

    /// <summary>
    /// Breaks the trace wherever a single step is physically impossible to have
    /// walked. The trace is sampled at a fixed rate, so a legitimate step is
    /// bounded: at the observed p99 speed that stays around 11 units, so
    /// anything beyond <see cref="AppConfig.MaxBridgeDistance"/> was never
    /// actually traversed and must not become a straight leg. Anything beyond
    /// <see cref="AppConfig.TeleportJumpDistance"/> is a real teleport.
    /// </summary>
    private static List<(List<RawPoint> Points, string PrecededBy)> SplitSegments(
        List<RawPoint> points, AppConfig cfg, List<SegmentBreak> breaks)
    {
        var segments = new List<(List<RawPoint>, string)>();
        var current = new List<RawPoint>();
        RawPoint? last = null;
        var bridge = Math.Max(1.0, cfg.MaxBridgeDistance);
        // The very first segment defines where the route begins, so BGI must
        // sync to that point; a teleport-type marker does exactly that.
        var precededBy = KindTeleport;

        foreach (var p in points)
        {
            if (last is not null)
            {
                var d = Distance(last, p);
                if (d > bridge)
                {
                    var dt = p.TimeSec - last.TimeSec;
                    var kind = d > cfg.TeleportJumpDistance ? KindTeleport : KindUnverified;
                    breaks.Add(new SegmentBreak
                    {
                        FromSec = last.TimeSec,
                        ToSec = p.TimeSec,
                        Distance = d,
                        ImpliedSpeed = dt > 1e-6 ? d / dt : double.PositiveInfinity,
                        Kind = kind
                    });
                    if (current.Count > 0)
                    {
                        segments.Add((current, precededBy));
                    }
                    current = new List<RawPoint>();
                    precededBy = kind;
                }
            }
            current.Add(p);
            last = p;
        }

        if (current.Count > 0)
        {
            segments.Add((current, precededBy));
        }

        return segments;
    }

    // ---------------------------------------------------------- simplification

    private static List<RawPoint> Simplify(List<RawPoint> segment, double tol, double maxChord, double cornerGuardDeg)
    {
        if (segment.Count <= 2)
        {
            return new List<RawPoint>(segment);
        }

        var keep = AdaptiveSplit(segment, tol, maxChord);
        keep = GreedyMerge(segment, keep, tol, maxChord, cornerGuardDeg);

        var result = new List<RawPoint>(keep.Count);
        foreach (var k in keep)
        {
            result.Add(segment[k]);
        }
        return result;
    }

    /// <summary>
    /// Recursively bisect a chord at its worst-deviating sample until the
    /// deviation is within tolerance and the chord is short enough. Only indices
    /// of real samples are returned.
    /// </summary>
    private static List<int> AdaptiveSplit(List<RawPoint> seg, double tol, double maxChord)
    {
        var keep = new bool[seg.Count];
        keep[0] = true;
        keep[^1] = true;

        var stack = new Stack<(int From, int To)>();
        stack.Push((0, seg.Count - 1));

        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            if (j - i < 2)
            {
                continue;
            }

            var maxDev = -1.0;
            var splitAt = -1;
            for (var k = i + 1; k < j; k++)
            {
                var d = PerpDistance(seg[k], seg[i], seg[j]);
                if (d > maxDev)
                {
                    maxDev = d;
                    splitAt = k;
                }
            }

            if (maxDev > tol || Distance(seg[i], seg[j]) > maxChord)
            {
                var s = splitAt >= 0 ? splitAt : (i + j) / 2;
                keep[s] = true;
                stack.Push((i, s));
                stack.Push((s, j));
            }
        }

        var indices = new List<int>();
        for (var k = 0; k < keep.Length; k++)
        {
            if (keep[k])
            {
                indices.Add(k);
            }
        }
        return indices;
    }

    /// <summary>
    /// Single left-to-right pass that drops a surviving vertex when the span
    /// between its neighbours still satisfies the tolerance. The tolerance is
    /// validated over EVERY sample in the span, so it stays a hard guarantee
    /// instead of accumulating across successive merges. Sharp corners are
    /// never merged away, because that is exactly the failure this replaces.
    /// </summary>
    private static List<int> GreedyMerge(List<RawPoint> seg, List<int> keep, double tol, double maxChord, double cornerGuardDeg)
    {
        var result = new List<int> { keep[0] };

        for (var n = 1; n < keep.Count - 1; n++)
        {
            var k = keep[n];
            var next = keep[n + 1];
            var prev = result[^1];

            if (Distance(seg[prev], seg[next]) <= maxChord &&
                SpanMaxDeviation(seg, prev, next) <= tol &&
                TurnAngle(seg[prev], seg[k], seg[next]) < cornerGuardDeg)
            {
                continue;
            }

            result.Add(k);
        }

        result.Add(keep[^1]);
        return result;
    }

    private static double SpanMaxDeviation(List<RawPoint> seg, int from, int to)
    {
        var max = 0.0;
        for (var k = from + 1; k < to; k++)
        {
            var d = PerpDistance(seg[k], seg[from], seg[to]);
            if (d > max)
            {
                max = d;
            }
        }
        return max;
    }

    /// <summary>Distance from p to the segment a-b, clamped so it is a true
    /// distance to the polyline leg rather than to an infinite line.</summary>
    private static double PerpDistance(RawPoint p, RawPoint a, RawPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12)
        {
            return Distance(p, a);
        }
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        var px = a.X + t * dx;
        var py = a.Y + t * dy;
        return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }

    /// <summary>Turning angle in degrees at b on the polyline a-b-c.</summary>
    private static double TurnAngle(RawPoint a, RawPoint b, RawPoint c)
    {
        var v1x = b.X - a.X;
        var v1y = b.Y - a.Y;
        var v2x = c.X - b.X;
        var v2y = c.Y - b.Y;
        var n1 = Math.Sqrt(v1x * v1x + v1y * v1y);
        var n2 = Math.Sqrt(v2x * v2x + v2y * v2y);
        if (n1 < 1e-9 || n2 < 1e-9)
        {
            return 0;
        }
        var cos = (v1x * v2x + v1y * v2y) / (n1 * n2);
        cos = Math.Clamp(cos, -1, 1);
        return Math.Acos(cos) * (180.0 / Math.PI);
    }
}
