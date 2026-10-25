namespace VideoPathTool.Core;

/// <summary>
/// One frame's observation of the cyan marker on the minimap.
/// </summary>
public class GlideMarkerObservation
{
    public double TimeSec { get; set; }

    /// <summary>Length/width ratio of the marker blob; high when the wing is deployed.</summary>
    public double AspectRatio { get; set; }

    /// <summary>Centroid of the marker in the minimap crop.</summary>
    public double CenterX { get; set; }

    public double CenterY { get; set; }

    /// <summary>Distance of the marker centroid from the minimap centre.</summary>
    public double DistanceFromCenter { get; set; }

    /// <summary>Pixel area of the marker blob. The flight trail is much larger.</summary>
    public double Area { get; set; }

    /// <summary>False when no marker could be located in this frame.</summary>
    public bool Valid { get; set; }
}

/// <summary>
/// A detected glide window.
/// </summary>
public class GlideSegment
{
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>Mean marker aspect ratio over the window; higher is more wing-like.</summary>
    public double Confidence { get; set; }

    /// <summary>How the segment was classified.</summary>
    public string Reason { get; set; } = string.Empty;
}
