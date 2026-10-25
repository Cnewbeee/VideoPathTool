using OpenCvSharp;

namespace VideoPathTool.Core;

using static MiniMapMatchConfig;

public class MapLayer
{
    public string LayerGroupId { get; set; } = string.Empty;
    public string LayerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public float Scale { get; set; } = 1;
    public int Floor { get; set; } = 0;
    public float Top { get; set; } = 0;
    public float Left { get; set; } = 0;
    public bool IsOverSize { get; set; } = false;
    public FastSqDiffMatcher? CoarseColorMatcher;
    public Mat FineGrayMap = new Mat();

    public void LoadLayer(string layerDir)
    {
        var colorMapPath = Path.Combine(layerDir, LayerId + "_color.webp");
        using (var colorStream = File.OpenRead(colorMapPath))
        using (var coarseColorMap = Mat.FromStream(colorStream, ImreadModes.Color))
        {
            CoarseColorMatcher = new FastSqDiffMatcher(coarseColorMap, new Size(RoughSize, RoughSize));
        }
        var grayMapFileName = LayerId + "_gray" + (IsOverSize ? ".png" : ".webp");
        var grayMapPath = Path.Combine(layerDir, grayMapFileName);
        using var grayStream = File.OpenRead(grayMapPath);
        FineGrayMap = Mat.FromStream(grayStream, ImreadModes.Grayscale);
    }

    public (Point2f, double) RoughMatch(Mat[] maskedMiniMaps, Mat maskF)
    {
        var (pos, val) = CoarseColorMatcher!.Match(maskedMiniMaps, maskF);
        return (MapToWorld(pos, RoughZoom, RoughSize), val);
    }

    public (Point2f, double) RoughMatch(Mat[] maskedMiniMaps, Mat maskF, Point2f preLoc, int[]? channels = null)
    {
        var roughPos = WorldToMap(preLoc, RoughZoom);
        var rect = GetRect(roughPos, (int)(RoughSearchRadius * Scale), RoughSize).Intersect(new Rect(0, 0, CoarseColorMatcher!.Source[0].Width, CoarseColorMatcher.Source[0].Height));
        if (rect.Width < RoughSize || rect.Height < RoughSize)
        {
            return (default, -1);
        }
        var (pos, val) = CoarseColorMatcher.Match(maskedMiniMaps, maskF, rect, channels);
        return (MapToWorld(rect.TopLeft + pos, RoughZoom, RoughSize), val);
    }

    public (Point2f, double) ExactMatch(Mat miniMap, Mat mask, Point2f preLoc, TemplateMatchModes mode = TemplateMatchModes.SqDiff)
    {
        var exactPos = WorldToMap(preLoc, ExactZoom);
        var rect = GetRect(exactPos, ExactSearchRadius, ExactSize).Intersect(new Rect(0, 0, FineGrayMap.Width, FineGrayMap.Height));
        if (rect.Width < ExactSize || rect.Height < ExactSize)
        {
            return (default, -1);
        }
        var bigMap = FineGrayMap[rect];
        var (pos, val) = TemplateMatchHelper.MatchTemplateSubPix(bigMap, miniMap, mode, mask);
        return (MapToWorld(rect.TopLeft + pos, ExactZoom, ExactSize), val);
    }

    private static Rect GetRect(Point loc, int halfSide, int miniMapSize)
    {
        return new Rect(loc.X - halfSide - miniMapSize / 2, loc.Y - halfSide - miniMapSize / 2, halfSide * 2 + miniMapSize, halfSide * 2 + miniMapSize);
    }

    private Point WorldToMap(Point2f pos, float zoom)
    {
        return new Point((int)Math.Round((Left - pos.X) * Scale / zoom), (int)Math.Round((Top - pos.Y) * Scale / zoom));
    }

    private Point2f MapToWorld(Point2f pos, float zoom, int miniMapSize)
    {
        return new Point2f(Left - (pos.X + miniMapSize / 2.0f) * zoom / Scale, Top - (pos.Y + miniMapSize / 2.0f) * zoom / Scale);
    }
}
