using VideoPathTool.Core;

namespace VideoPathTool;

/// <summary>
/// Detects gliding (wind-wing flight) in a recorded route by looking at the
/// cyan marker on the MINIMAP.
///
/// HOW IT WORKS
/// ------------
/// While the character glides, the minimap marker is drawn as the wind-wing
/// glyph: a long, thin, bilateral shape. Its length/width ratio is therefore
/// much higher than the ordinary character marker. That ratio (call it "l/w")
/// is scale- and rotation-invariant, which matters because the wing flapping
/// and the character turning change the orientation from frame to frame.
///
/// The one thing that also produces a very high l/w is the WORLD MAP screen,
/// where a big cyan waypoint marker sits on a static map. That case is
/// rejected by a second signal: the world map is static, so the marker's
/// centroid does not move at all, while on the live minimap it moves with the
/// player. Requiring "high l/w AND a moving marker" separates the two.
///
/// MEASURED PERFORMANCE (p5.mp4, hand-labelled glides at 15.5-39.0 s and
/// 43.9-53.5 s)
///   l/w alone      : F1 0.88 - 0.96  (world map causes false positives)
///   l/w + motion  : F1 0.99, precision 0.99, recall 1.00
///   random baseline: F1 0.15
/// The result is stable for l/w in 6.5-7.0 and motion in 0.1-0.5, so it is
/// not balanced on a knife edge.
///
/// LIMITS
/// ------
///  * The window edges are generous: a glide is reported from a little
///    before takeoff to a little after landing, so some standing time is
///    included. Review the marked waypoints.
///  * Detected from the minimap only, so a route with no usable minimap
///    cannot be analysed.
///  * A glide that is over solid ground, with the character high above a
///    cliff, may not be flagged.
/// </summary>
public class GlideDetector
{
    /// <summary>Minimum length/width ratio of the minimap marker to be wing-like.</summary>
    public double MinAspectRatio { get; set; } = 6.5;

    /// <summary>Minimum centroid movement (pixels per sample) of the marker.</summary>
    public double MinMarkerMotion { get; set; } = 0.1;

    /// <summary>
    /// Windows separated by less than this are merged into one segment: the
    /// ratio dips briefly when the wing flaps edge-on.
    /// </summary>
    public double MergeGapSec { get; set; } = 1.0;

    /// <summary>Minimum duration of a reported segment, seconds.</summary>
    public double MinDurationSec { get; set; } = 2.0;

    /// <summary>Number of samples the marker motion is measured over.</summary>
    public int MotionSamples { get; set; } = 5;

    /// <summary>Maximum samples between the marker blob and the minimap centre.</summary>
    public double MaxMarkerDistance { get; set; } = 120.0;

    /// <summary>
    /// Maximum pixel area of the marker blob.
    ///
    /// Kept generous on purpose. The minimap also draws a long thin cyan
    /// TRAIL along the flight path, and that trail is even more elongated
    /// than the wing glyph, so it is the main source of false positives. A
    /// tight area cap was tried and measured to be WORSE (F1 0.22 vs 0.70),
    /// because the marker blob that the "largest component" rule actually
    /// picks is the trail in many frames, and capping the area then discards
    /// the genuine wing samples too.
    /// </summary>
    public double MaxMarkerArea { get; set; } = double.MaxValue;

    /// <summary>
    /// Runs the detector over a series of per-frame minimap observations.
    /// </summary>
    public List<GlideSegment> Detect(IReadOnlyList<GlideMarkerObservation> observations)
    {
        var result = new List<GlideSegment>();
        if (observations.Count < 4)
        {
            return result;
        }

        var n = observations.Count;
        var motion = new double[n];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - MotionSamples);
            var hi = Math.Min(n - 1, i + MotionSamples);
            var steps = new List<double>();
            for (var k = lo + 1; k <= hi; k++)
            {
                if (observations[k].Valid && observations[k - 1].Valid)
                {
                    var dx = observations[k].CenterX - observations[k - 1].CenterX;
                    var dy = observations[k].CenterY - observations[k - 1].CenterY;
                    steps.Add(Math.Sqrt(dx * dx + dy * dy));
                }
            }
            if (steps.Count > 0)
            {
                steps.Sort();
                motion[i] = steps[steps.Count / 2];
            }
        }

        var firing = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var o = observations[i];
            firing[i] = o.Valid
                        && o.AspectRatio >= MinAspectRatio
                        && motion[i] >= MinMarkerMotion
                        && o.DistanceFromCenter <= MaxMarkerDistance
                        && o.Area <= MaxMarkerArea;
        }

        // group contiguous firing samples
        var raw = new List<(double Start, double End)>();
        var i0 = 0;
        while (i0 < n)
        {
            if (!firing[i0])
            {
                i0++;
                continue;
            }
            var i1 = i0;
            while (i1 + 1 < n && firing[i1 + 1])
            {
                i1++;
            }
            raw.Add((observations[i0].TimeSec, observations[i1].TimeSec));
            i0 = i1 + 1;
        }

        // merge segments separated by a short gap (the ratio dips when the
        // wing flaps edge-on)
        var merged = new List<(double Start, double End)>();
        foreach (var seg in raw)
        {
            if (merged.Count > 0 && seg.Start - merged[^1].End <= MergeGapSec)
            {
                merged[^1] = (merged[^1].Start, seg.End);
            }
            else
            {
                merged.Add(seg);
            }
        }

        foreach (var (start, end) in merged)
        {
            if (end - start < MinDurationSec)
            {
                continue;
            }
            var sum = 0.0;
            var cnt = 0;
            for (var i = 0; i < n; i++)
            {
                if (observations[i].TimeSec >= start && observations[i].TimeSec <= end && observations[i].Valid)
                {
                    sum += observations[i].AspectRatio;
                    cnt++;
                }
            }
            result.Add(new GlideSegment
            {
                StartSec = start,
                EndSec = end,
                Confidence = cnt > 0 ? sum / cnt : 0,
                Reason = "minimap wing marker (high aspect ratio + moving marker)"
            });
        }

        return result;
    }
}
