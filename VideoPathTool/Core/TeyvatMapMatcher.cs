using OpenCvSharp;
using System.Text.Json;

namespace VideoPathTool.Core;

using static MiniMapMatchConfig;

public class TeyvatMapMatcher : IDisposable
{
    public const int GameMapRows = 19;
    public const int GameMapCols = 22;
    public const int GameMapUpRows = 11;
    public const int GameMapLeftCols = 15;
    public const int TeyvatMapImageBlockWidth = 2048;
    public static readonly Point2f MapOriginInImageCoordinate = new((GameMapLeftCols + 1) * TeyvatMapImageBlockWidth, (GameMapUpRows + 1) * TeyvatMapImageBlockWidth);
    public const float MapImageBlockWidthScale = TeyvatMapImageBlockWidth / 1024f;

    public List<MapLayer> Layers { get; } = new();
    public MatchResult PrevSuccessResult;
    public MatchResult LastMatch { get; private set; }
    private readonly MiniMapPreprocessor _preprocessor = new();

    public struct MatchResult
    {
        public MapLayer? Layer = null;
        public Point2f MapPos = new Point2f(0, 0);
        public double Confidence = 0;

        public MatchResult() { }

        public readonly bool IsSuccess(int rank)
        {
            var index = Math.Clamp(rank, 0, ConfidenceThresholds.Length - 1);
            return Confidence <= 1 && Confidence >= ConfidenceThresholds[index];
        }
    }

    public int LoadLayers(string layerDir)
    {
        var seen = new HashSet<string>();
        foreach (var jsonFile in Directory.GetFiles(layerDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            List<MapLayer>? tempLayers;
            try
            {
                tempLayers = JsonSerializer.Deserialize<List<MapLayer>>(File.ReadAllText(jsonFile));
            }
            catch
            {
                continue;
            }
            if (tempLayers == null)
            {
                continue;
            }
            foreach (var layer in tempLayers)
            {
                if (string.IsNullOrEmpty(layer.LayerId) || !seen.Add(layer.LayerId))
                {
                    continue;
                }
                var colorMap = Path.Combine(layerDir, layer.LayerId + "_color.webp");
                var grayMap = Path.Combine(layerDir, layer.LayerId + "_gray" + (layer.IsOverSize ? ".png" : ".webp"));
                if (!File.Exists(colorMap) || !File.Exists(grayMap))
                {
                    continue;
                }
                layer.LoadLayer(layerDir);
                Layers.Add(layer);
            }
        }
        return Layers.Count;
    }

    public Point2f ConvertGenshinMapCoordinatesToImageCoordinates(Point2f genshinMapCoordinates)
    {
        return new Point2f(MapOriginInImageCoordinate.X - genshinMapCoordinates.X * MapImageBlockWidthScale,
            MapOriginInImageCoordinate.Y - genshinMapCoordinates.Y * MapImageBlockWidthScale);
    }

    public Point2f ConvertImageCoordinatesToGenshinMapCoordinates(Point2f imageCoordinates)
    {
        return new Point2f((MapOriginInImageCoordinate.X - imageCoordinates.X) / MapImageBlockWidthScale,
            (MapOriginInImageCoordinate.Y - imageCoordinates.Y) / MapImageBlockWidthScale);
    }

    public void Reset()
    {
        PrevSuccessResult = default;
    }

    public (Point2f Pos, double Confidence, bool Success) Match(Mat colorMiniMapMat, Point2f? prevGenshin, int rank = 2, bool allowGlobal = true,
        Point2f? markerOffsetCrop = null)
    {
        var result = new MatchResult();
        var (miniMap, mask) = _preprocessor.GetMiniMapAndMask(colorMiniMapMat);
        using (miniMap)
        using (mask)
        {
            if (prevGenshin is { X: > 0, Y: > 0 })
            {
                LocalMatch(miniMap, mask, prevGenshin.Value, ref result);
                if (!result.IsSuccess(rank) && allowGlobal)
                {
                    GlobalMatch(miniMap, mask, ref result);
                }
            }
            else if (allowGlobal)
            {
                GlobalMatch(miniMap, mask, ref result);
            }
        }

        LastMatch = result;
        if (result.Layer != null && result.MapPos != default)
        {
            if (markerOffsetCrop is Point2f off)
            {
                var offTmplX = off.X * (float)ExactSize / OriginalSize;
                var offTmplY = off.Y * (float)ExactSize / OriginalSize;
                var layerScale = result.Layer.Scale <= 0 ? 1f : result.Layer.Scale;
                result.MapPos = new Point2f(
                    result.MapPos.X - offTmplX * ExactZoom / layerScale,
                    result.MapPos.Y - offTmplY * ExactZoom / layerScale);
            }
            PrevSuccessResult = result;
        }

        return (result.MapPos, result.Confidence, result.Layer != null && result.IsSuccess(rank));
    }

    public void GlobalMatch(Mat miniMap, Mat mask, ref MatchResult result)
    {
        using var context = new MatchContext(miniMap, mask);
        RoughMatchGlobal(context, ref result);
        ExactMatch(context, ref result);
    }

    public void LocalMatch(Mat miniMap, Mat mask, Point2f pos, ref MatchResult result)
    {
        using var context = new MatchContext(miniMap, mask);
        RoughMatchLocal(context, pos, ref result);
        ExactMatch(context, ref result);
    }

    public void RoughMatchGlobal(MatchContext context, ref MatchResult result)
    {
        foreach (var layer in Layers)
        {
            var (tempPos, tempVal) = layer.RoughMatch(context.MaskedMiniMapRoughs, context.MaskRoughF);
            if (!context.NormalizerRough.Update(tempVal + context.TplSumSq)) continue;
            result.Layer = layer;
            result.MapPos = tempPos;
        }
        result.Confidence = context.NormalizerRough.Confidence();
    }

    public void RoughMatchLocal(MatchContext context, Point2f pos, ref MatchResult result)
    {
        result.MapPos = pos;
        if (PrevSuccessResult.MapPos.Equals(pos))
        {
            result.Layer = PrevSuccessResult.Layer;
        }
        if (result.Layer != null)
        {
            var (tempPos, tempVal) = result.Layer.RoughMatch(context.MaskedMiniMapRoughs, context.MaskRoughF, pos);
            if (tempPos != default && context.NormalizerRough.Update(tempVal + context.TplSumSq))
            {
                result.MapPos = tempPos;
                result.Confidence = context.NormalizerRough.Confidence();
            }
        }

        if (result.IsSuccess(0))
        {
            return;
        }

        var flag = false;
        foreach (var layer in (result.Layer == null) ? Layers : Layers.Where(layer => layer != PrevSuccessResult.Layer))
        {
            var (tempPos, tempVal) = layer.RoughMatch(context.MaskedMiniMapRoughs, context.MaskRoughF, pos);
            if (tempPos == default || !context.NormalizerRough.Update(tempVal + context.TplSumSq)) continue;
            result.Layer = layer;
            result.MapPos = tempPos;
            flag = true;
        }
        if (flag) result.Confidence = context.NormalizerRough.Confidence();
    }

    public void ExactMatch(MatchContext context, ref MatchResult result)
    {
        if (result.Layer == null || !result.IsSuccess(2)) return;
        var (tempPos, tempVal) = result.Layer.ExactMatch(context.MiniMapExact, context.MaskExact, result.MapPos);
        if (tempPos != default && context.NormalizerExact.Update(tempVal))
        {
            result.MapPos = tempPos;
            result.Confidence = context.NormalizerExact.Confidence();
        }
        else
        {
            result.Confidence = 0;
        }
    }

    public (float Angle, float Confidence) PredictRotation(Mat colorMiniMapMat)
    {
        return _preprocessor.PredictRotationWithConfidence(colorMiniMapMat);
    }

    public void DrawDebug(Mat colorMiniMapMat, string outputPath)
    {
        var (miniMap, mask) = _preprocessor.GetMiniMapAndMask(colorMiniMapMat);
        using (miniMap)
        using (mask)
        {
            Cv2.ImWrite(outputPath, miniMap);
        }
    }

    public void Dispose()
    {
        _preprocessor.Dispose();
        foreach (var layer in Layers)
        {
            layer.CoarseColorMatcher?.Dispose();
            layer.FineGrayMap.Dispose();
        }
    }
}
