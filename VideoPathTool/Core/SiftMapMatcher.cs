using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace VideoPathTool.Core;

public class SiftMapMatcher : IDisposable
{
    public const float MapOriginX = 32768f;
    public const float MapOriginY = 24576f;
    public const float MapScale = 2f;

    private Feature2D? _sift;
    private DescriptorMatcher? _matcher;
    private KeyPoint[] _trainKeyPoints = [];
    private Mat? _trainDescriptors;
    private bool _loaded;

    public int LastGoodMatches { get; private set; }
    public int LastInliers { get; private set; }
    public int LastUniqueInliers { get; private set; }
    public double LastScale { get; private set; }
    public int TrainKeyPointCount => _trainKeyPoints.Length;
    public bool IsLoaded => _loaded;

    public void Load(string keyPointPath, string descriptorPath)
    {
        if (_loaded)
        {
            return;
        }
        if (!File.Exists(keyPointPath) || !File.Exists(descriptorPath))
        {
            throw new FileNotFoundException($"SIFT 特征文件缺失: {keyPointPath}");
        }

        var bytes = File.ReadAllBytes(keyPointPath);
        var sizeOfKeyPoint = Marshal.SizeOf<KeyPoint>();
        if (sizeOfKeyPoint != 28)
        {
            throw new InvalidOperationException($"KeyPoint 结构大小异常: {sizeOfKeyPoint}");
        }
        if (bytes.Length % sizeOfKeyPoint != 0)
        {
            throw new InvalidDataException("无法识别的 KeyPoint 文件格式");
        }
        _trainKeyPoints = MemoryMarshal.Cast<byte, KeyPoint>(bytes.AsSpan()).ToArray();

        using (var descImage = new Mat(descriptorPath, ImreadModes.Grayscale))
        {
            _trainDescriptors = new Mat();
            descImage.ConvertTo(_trainDescriptors, MatType.CV_32FC1);
        }

        _sift = SIFT.Create();
        _matcher = DescriptorMatcher.Create("FlannBased");
        _matcher.Add(new[] { _trainDescriptors });
        _matcher.Train();
        _loaded = true;
    }

    public (Point2f World, double Confidence, bool Success) Match(Mat grayMiniMap, int minInliers = 5, int strongInliers = 10, Point2f? refInQuery = null)
    {
        LastGoodMatches = 0;
        LastInliers = 0;
        LastUniqueInliers = 0;
        LastScale = 0;
        if (!_loaded || _sift == null || _matcher == null)
        {
            return (default, 0, false);
        }

        using var queryDescriptors = new Mat();
        _sift.DetectAndCompute(grayMiniMap, null, out var queryKeyPoints, queryDescriptors);
        if (queryKeyPoints.Length < 7 || queryDescriptors.Empty())
        {
            return (default, 0, false);
        }

        var matches = _matcher.KnnMatch(queryDescriptors, 2);
        var good = new List<DMatch>();
        foreach (var match in matches)
        {
            if (match.Length == 2 && match[0].Distance < 0.75 * match[1].Distance)
            {
                good.Add(match[0]);
            }
        }
        LastGoodMatches = good.Count;
        if (good.Count < 7)
        {
            return (default, 0, false);
        }

        var src = good.Select(m => queryKeyPoints[m.QueryIdx].Pt).ToArray();
        var dst = good.Select(m => _trainKeyPoints[m.TrainIdx].Pt).ToArray();
        using var srcMat = Mat.FromArray(src.Select(p => new Point2d(p.X, p.Y)));
        using var dstMat = Mat.FromArray(dst.Select(p => new Point2d(p.X, p.Y)));
        using var mask = new Mat();
        using var affine = Cv2.EstimateAffinePartial2D(srcMat, dstMat, mask,
            RobustEstimationAlgorithms.RANSAC, 3.0, 2000, 0.995, 10);
        if (affine.Empty())
        {
            return (default, 0, false);
        }

        LastInliers = Cv2.CountNonZero(mask);
        var m00 = affine.At<double>(0, 0);
        var m01 = affine.At<double>(0, 1);
        var m10 = affine.At<double>(1, 0);
        var scale = Math.Sqrt(m00 * m00 + m10 * m10);
        if (scale < 0.4 || scale > 8.0 || LastInliers < minInliers)
        {
            return (default, 0, false);
        }

        var unique = new HashSet<(int, int)>();
        for (var i = 0; i < good.Count; i++)
        {
            if (mask.At<byte>(i, 0) != 0)
            {
                var p = src[i];
                unique.Add(((int)Math.Round(p.X), (int)Math.Round(p.Y)));
            }
        }
        LastUniqueInliers = unique.Count;
        if (LastUniqueInliers < 4)
        {
            return (default, 0, false);
        }

        var cx = refInQuery?.X ?? grayMiniMap.Cols / 2.0;
        var cy = refInQuery?.Y ?? grayMiniMap.Rows / 2.0;
        var px = m00 * cx + m01 * cy + affine.At<double>(0, 2);
        var py = m10 * cx + affine.At<double>(1, 1) * cy + affine.At<double>(1, 2);
        var world = new Point2f((MapOriginX - (float)px) / MapScale, (MapOriginY - (float)py) / MapScale);
        var confidence = Math.Min(1.0, LastInliers / 20.0);
        LastScale = scale;
        _ = strongInliers;
        return (world, confidence, true);
    }

    public void Dispose()
    {
        _matcher?.Dispose();
        _sift?.Dispose();
        _trainDescriptors?.Dispose();
    }
}
