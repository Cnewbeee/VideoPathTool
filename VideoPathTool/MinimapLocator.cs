using OpenCvSharp;

namespace VideoPathTool;

public class MinimapCircle
{
    public int CenterX { get; set; }
    public int CenterY { get; set; }
    public int Radius { get; set; }
    public int Votes { get; set; }
}

public static class MinimapLocator
{
    public static MinimapCircle? Detect(Mat frame, int searchSize, int minRadius, int maxRadius, double param2 = 50)
    {
        var size = Math.Min(searchSize, Math.Min(frame.Width, frame.Height));
        if (size < 50)
        {
            return null;
        }
        using var roi = new Mat(frame, new Rect(0, 0, size, size));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MedianBlur(gray, gray, 5);
        var circles = Cv2.HoughCircles(gray, HoughModes.Gradient, 1.2, size / 2.0,
            param1: 100, param2: param2, minRadius: minRadius, maxRadius: maxRadius);
        if (circles.Length == 0)
        {
            return null;
        }
        var best = circles[0];
        foreach (var c in circles)
        {
            if (c.Radius > best.Radius)
            {
                best = c;
            }
        }
        return new MinimapCircle
        {
            CenterX = (int)Math.Round(best.Center.X),
            CenterY = (int)Math.Round(best.Center.Y),
            Radius = (int)Math.Round(best.Radius),
            Votes = circles.Length
        };
    }

    public static MinimapCircle? DetectFromVideo(string videoPath, int searchSize, int minRadius, int maxRadius,
        int sampleCount, double param2, Action<string> log)
    {
        using var capture = new VideoCapture(videoPath);
        if (!capture.IsOpened())
        {
            log($"无法打开视频: {videoPath}");
            return null;
        }
        var totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        var detections = new List<MinimapCircle>();
        using var frame = new Mat();
        for (var i = 0; i < sampleCount; i++)
        {
            var pos = (int)((totalFrames - 1) * (i + 0.5) / sampleCount);
            capture.Set(VideoCaptureProperties.PosFrames, pos);
            if (!capture.Read(frame) || frame.Empty())
            {
                continue;
            }
            var circle = Detect(frame, searchSize, minRadius, maxRadius, param2);
            if (circle != null)
            {
                detections.Add(circle);
                log($"  采样帧 {i + 1}/{sampleCount} (帧 {pos}): 圆心=({circle.CenterX},{circle.CenterY}) 半径={circle.Radius}");
            }
            else
            {
                log($"  采样帧 {i + 1}/{sampleCount} (帧 {pos}): 未检测到圆形");
            }
        }
        if (detections.Count == 0)
        {
            return null;
        }
        return new MinimapCircle
        {
            CenterX = Median(detections.Select(d => d.CenterX)),
            CenterY = Median(detections.Select(d => d.CenterY)),
            Radius = Median(detections.Select(d => d.Radius)),
            Votes = detections.Count
        };
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    public static Mat ExtractMinimap(Mat frame, int centerX, int centerY, int radius, double cropRatio)
    {
        var side = (int)Math.Round(radius * 2 * cropRatio);
        side = Math.Max(8, side);
        var left = centerX - side / 2;
        var top = centerY - side / 2;
        var padLeft = Math.Max(0, -left);
        var padTop = Math.Max(0, -top);
        var padRight = Math.Max(0, left + side - frame.Width);
        var padBottom = Math.Max(0, top + side - frame.Height);
        using var padded = new Mat();
        if (padLeft > 0 || padTop > 0 || padRight > 0 || padBottom > 0)
        {
            Cv2.CopyMakeBorder(frame, padded, padTop, padBottom, padLeft, padRight, BorderTypes.Replicate);
        }
        else
        {
            frame.CopyTo(padded);
        }
        var x = left + padLeft;
        var y = top + padTop;
        using var crop = new Mat(padded, new Rect(x, y, side, side));
        var output = new Mat();
        Cv2.Resize(crop, output, new Size(156, 156), 0, 0, InterpolationFlags.Area);
        return output;
    }
}
