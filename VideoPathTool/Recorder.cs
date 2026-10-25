using System.Diagnostics;
using OpenCvSharp;
using VideoPathTool.Core;

namespace VideoPathTool;

public class RecordStats
{
    public int SampledFrames { get; set; }
    public int MatchedFrames { get; set; }
    public int FailedFrames { get; set; }
    public int InvalidFrames { get; set; }
}

public class Recorder
{
    private readonly AppConfig _cfg;
    private readonly TeyvatMapMatcher _matcher;
    private readonly SiftMapMatcher? _sift;
    private readonly Action<string> _log;

    public Recorder(AppConfig cfg, TeyvatMapMatcher matcher, SiftMapMatcher? sift, Action<string> log)
    {
        _cfg = cfg;
        _matcher = matcher;
        _sift = sift;
        _log = log;
    }

    /// <summary>
    /// Walks the video and records the position of every sampled frame.
    ///
    /// <paramref name="pause"/> lets a frontend suspend the run between frames
    /// so settings can be changed and the work resumed without losing what was
    /// already recorded. <paramref name="liveCircle"/> is re-read on every
    /// frame, so a minimap-correction takes effect immediately instead of
    /// requiring a restart. Both are optional; the console frontend passes
    /// neither.
    /// </summary>
    public (List<RawPoint> Points, List<RawPoint> AllSamples) Run(
        MinimapCircle circle,
        CancellationToken ct,
        out RecordStats stats,
        PauseToken? pause = null,
        IProgress<RecordTick>? progress = null,
        Func<MinimapCircle>? liveCircle = null)
    {
        stats = new RecordStats();
        var points = new List<RawPoint>();
        var allSamples = new List<RawPoint>();
        using var capture = new VideoCapture(_cfg.VideoPath);
        if (!capture.IsOpened())
        {
            _log($"无法打开视频: {_cfg.VideoPath}");
            return (points, allSamples);
        }

        var fps = capture.Get(VideoCaptureProperties.Fps);
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
        {
            fps = 30;
        }
        var totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        var startFrame = (int)Math.Max(0, _cfg.StartTimeSec * fps);
        var endFrame = _cfg.EndTimeSec > 0 ? (int)Math.Min(totalFrames, _cfg.EndTimeSec * fps) : totalFrames;
        if (endFrame <= startFrame)
        {
            endFrame = totalFrames;
        }
        if (startFrame > 0)
        {
            capture.Set(VideoCaptureProperties.PosFrames, startFrame);
        }

        Directory.CreateDirectory(_cfg.PreviewDir);
        using var frame = new Mat();
        Point2f? prev = null;
        var prevTimeSec = 0.0;
        var lastInvalidSec = -999.0;
        Mat? lastAcceptedGray = null;
        var index = startFrame;
        var nextPreviewSec = _cfg.StartTimeSec;
        var failedPreviews = 0;
        var lastProgressSec = -999.0;
        var sw = Stopwatch.StartNew();
        var recentMarkerOffsets = new Queue<Point2f>();
        var totalSec = endFrame / fps;

        while (index < endFrame && !ct.IsCancellationRequested)
        {
            // Suspend here, between frames, so the state on resume is exactly
            // the state on pause: one sample has been fully processed.
            pause?.Wait(ct);

            // Re-read the tunables each frame. This is what makes "pause, fix
            // the minimap circle, resume" work without losing progress.
            var step = Math.Max(1, (int)Math.Round(fps / Math.Max(0.1, _cfg.SampleFps)));
            var curCircle = liveCircle?.Invoke() ?? circle;

            var isSample = (index - startFrame) % step == 0;
            if (isSample)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    break;
                }
            }
            else
            {
                if (!capture.Grab())
                {
                    break;
                }
                index++;
                continue;
            }

            var timeSec = index / fps;
            using (var minimap = MinimapLocator.ExtractMinimap(frame, curCircle.CenterX, curCircle.CenterY, curCircle.Radius, _cfg.CropRatio))
            {
                stats.SampledFrames++;
                using var grayMinimap = new Mat();
                Cv2.CvtColor(minimap, grayMinimap, ColorConversionCodes.BGR2GRAY);
                var hasMarker = HasPlayerMarker(minimap, out _);
                var validImage = IsValidMinimap(grayMinimap, out var grayStd, out var lapVar);
                if (!hasMarker || !validImage)
                {
                    stats.InvalidFrames++;
                    lastInvalidSec = timeSec;
                    if (failedPreviews < _cfg.MaxFailedPreviews)
                    {
                        failedPreviews++;
                        Cv2.ImWrite(Path.Combine(_cfg.PreviewDir, $"invalid_{timeSec:F0}s_std{grayStd:F0}_lap{lapVar:F0}.png"), minimap);
                    }
                }
                else
                {
                    var pos = default(Point2f);
                    var conf = 0.0;
                    var accepted = false;
                    var source = "none";
                    var markerOffsetCrop = GetMarkerOffsetCrop(frame, curCircle);
                    if (markerOffsetCrop is Point2f md)
                    {
                        recentMarkerOffsets.Enqueue(md);
                        while (recentMarkerOffsets.Count > 61)
                        {
                            recentMarkerOffsets.Dequeue();
                        }
                    }
                    markerOffsetCrop = MedianOffset(recentMarkerOffsets);
                    Point2f? siftRef = markerOffsetCrop is Point2f oc
                        ? new Point2f(MiniMapMatchConfig.OriginalSize / 2f + oc.X,
                            MiniMapMatchConfig.OriginalSize / 2f + oc.Y)
                        : null;

                    if (_sift is { IsLoaded: true })
                    {
                        var (siftPos, siftConf, siftOk) = _sift.Match(grayMinimap, _cfg.SiftMinInliers, _cfg.SiftStrongInliers, siftRef);
                        if (siftOk && siftPos != default)
                        {
                            var strong = _sift.LastInliers >= _cfg.SiftStrongInliers;
                            if (strong || IsPlausible(prev, prevTimeSec, siftPos, timeSec))
                            {
                                pos = siftPos;
                                conf = siftConf;
                                accepted = true;
                                source = $"sift{_sift.LastInliers}";
                            }
                        }
                    }

                    if (!accepted)
                    {
                        var (templatePos, templateConf, templateOk) = _matcher.Match(minimap, prev, _cfg.MatchRank, allowGlobal: true,
                            markerOffsetCrop: markerOffsetCrop);
                        var layerId = _matcher.LastMatch.Layer?.LayerId ?? string.Empty;
                        var isMapBack = layerId.StartsWith("MapBack", StringComparison.OrdinalIgnoreCase);
                        var canResync = timeSec - prevTimeSec >= _cfg.ResyncGapSec &&
                                        timeSec - lastInvalidSec <= _cfg.ResyncWindowSec;
                        var requiredConfidence = canResync
                            ? Math.Max(_cfg.ConfidenceThreshold, _cfg.ResyncConfidence)
                            : _cfg.ConfidenceThreshold;
                        if (templateOk && !isMapBack && templateConf >= requiredConfidence &&
                            (canResync || IsPlausible(prev, prevTimeSec, templatePos, timeSec)))
                        {
                            pos = templatePos;
                            conf = templateConf;
                            accepted = true;
                            source = canResync ? $"template-resync:{layerId}" : $"template:{layerId}";
                        }
                    }

                    var similarity = lastAcceptedGray == null ? -1 : ComputeSimilarity(grayMinimap, lastAcceptedGray);
                    if (accepted && prev is Point2f prevPoint && similarity >= _cfg.HoldSimilarity &&
                        Distance(pos, prevPoint) > _cfg.HoldMaxDistance)
                    {
                        pos = prevPoint;
                        source += ":hold";
                    }
                    else if (!accepted && prev is Point2f holdPoint && similarity >= _cfg.HoldSimilarity)
                    {
                        pos = holdPoint;
                        accepted = true;
                        source = "hold";
                    }

                    allSamples.Add(new RawPoint(timeSec, pos.X, pos.Y, conf) { Fresh = accepted });

                    if (accepted)
                    {
                        stats.MatchedFrames++;
                        points.Add(new RawPoint(timeSec, pos.X, pos.Y, conf) { Fresh = true });
                        prev = pos;
                        prevTimeSec = timeSec;
                        lastAcceptedGray?.Dispose();
                        lastAcceptedGray = grayMinimap.Clone();
                    }
                    else
                    {
                        stats.FailedFrames++;
                    }

                    if (timeSec >= nextPreviewSec)
                    {
                        nextPreviewSec += Math.Max(5, _cfg.PreviewEverySec);
                        var tag = accepted ? "ok" : "fail";
                        var name = $"{tag}_{timeSec:F0}s_{source}_c{conf:F3}.png";
                        Cv2.ImWrite(Path.Combine(_cfg.PreviewDir, name), minimap);
                    }
                    else if (!accepted && failedPreviews < _cfg.MaxFailedPreviews)
                    {
                        failedPreviews++;
                        var name = $"fail_{timeSec:F0}s_c{conf:F3}.png";
                        Cv2.ImWrite(Path.Combine(_cfg.PreviewDir, name), minimap);
                    }
                }
            }

            if (timeSec - lastProgressSec >= 30)
            {
                lastProgressSec = timeSec;
                var elapsed = sw.Elapsed;
                _log($"[进度] 视频 {timeSec:F0}s / {(endFrame / fps):F0}s | 采样 {stats.SampledFrames} 成功 {stats.MatchedFrames} 失败 {stats.FailedFrames} | 已用 {elapsed.TotalSeconds:F0}s");
            }

            // A UI wants a steady stream of small updates; the console only
            // needs the 30 s line above.
            progress?.Report(new RecordTick(
                timeSec, totalSec,
                stats.SampledFrames, stats.MatchedFrames, stats.FailedFrames, stats.InvalidFrames,
                points.Count, sw.Elapsed.TotalSeconds));

            index++;
        }

        return (points, allSamples);
    }

    private static Point2f? MedianOffset(Queue<Point2f> offsets)
    {
        if (offsets.Count == 0)
        {
            return null;
        }
        var xs = offsets.Select(o => o.X).OrderBy(v => v).ToArray();
        var ys = offsets.Select(o => o.Y).OrderBy(v => v).ToArray();
        return new Point2f(xs[xs.Length / 2], ys[ys.Length / 2]);
    }

    private Point2f? GetMarkerOffsetCrop(Mat frame, MinimapCircle circle)
    {
        if (!_cfg.UsePlayerMarker)
        {
            return null;
        }
        return DetectPlayerMarkerOffset(frame, circle.CenterX, circle.CenterY, _cfg.MarkerSearchRadius,
            Math.Max(8, (int)Math.Round(circle.Radius * 2 * _cfg.CropRatio)));
    }

    public static Point2f? DetectPlayerMarkerOffset(Mat frame, int centerX, int centerY, int searchRadius, int cropSide)
    {
        searchRadius = Math.Max(10, searchRadius);
        var x0 = Math.Clamp(centerX - searchRadius, 0, Math.Max(0, frame.Width - 1));
        var y0 = Math.Clamp(centerY - searchRadius, 0, Math.Max(0, frame.Height - 1));
        var x1 = Math.Clamp(centerX + searchRadius, 0, frame.Width);
        var y1 = Math.Clamp(centerY + searchRadius, 0, frame.Height);
        var w = x1 - x0;
        var h = y1 - y0;
        if (w < 10 || h < 10)
        {
            return null;
        }
        using var roi = new Mat(frame, new Rect(x0, y0, w, h));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(85, 225, 205), new Scalar(96, 255, 255), mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var bestIndex = -1;
        double bestArea = 0;
        for (var i = 1; i < count; i++)
        {
            var area = stats.At<int>(i, 4);
            if (area < 30 || area > 5000)
            {
                continue;
            }
            var cx = centroids.At<double>(i, 0) + x0;
            var cy = centroids.At<double>(i, 1) + y0;
            var dx = cx - centerX;
            var dy = cy - centerY;
            if (Math.Sqrt(dx * dx + dy * dy) > searchRadius * 0.9)
            {
                continue;
            }
            if (area > bestArea)
            {
                bestArea = area;
                bestIndex = i;
            }
        }
        if (bestIndex < 0)
        {
            return null;
        }
        var mx = centroids.At<double>(bestIndex, 0) + x0 - centerX;
        var my = centroids.At<double>(bestIndex, 1) + y0 - centerY;
        var scale = (float)MiniMapMatchConfig.OriginalSize / Math.Max(8, cropSide);
        return new Point2f((float)(mx * scale), (float)(my * scale));
    }

    private bool IsPlausible(Point2f? prev, double prevTimeSec, Point2f pos, double timeSec)
    {
        if (prev is not Point2f p)
        {
            return true;
        }
        var dt = Math.Clamp(timeSec - prevTimeSec, 0.5, 5.0);
        var maxDistance = _cfg.MaxMoveSpeed * dt + _cfg.MoveMargin;
        var dx = pos.X - p.X;
        var dy = pos.Y - p.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= maxDistance;
    }

    private static double ComputeSimilarity(Mat currentGray, Mat previousGray)
    {
        if (currentGray.Size() != previousGray.Size())
        {
            return -1;
        }
        using var result = new Mat();
        Cv2.MatchTemplate(currentGray, previousGray, result, TemplateMatchModes.CCoeffNormed);
        return result.Empty() ? -1 : result.At<float>(0, 0);
    }

    private static double Distance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private bool HasPlayerMarker(Mat bgr, out double offset)
    {
        offset = 0;
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var cyan = new Mat();
        Cv2.InRange(hsv, new Scalar(80, 120, 120), new Scalar(100, 255, 255), cyan);
        var cx = bgr.Width / 2;
        var cy = bgr.Height / 2;
        var radius = Math.Min(bgr.Width, bgr.Height) * 0.225;
        var radiusSq = radius * radius;
        var x0 = Math.Max(0, (int)(cx - radius));
        var x1 = Math.Min(bgr.Width - 1, (int)(cx + radius));
        var y0 = Math.Max(0, (int)(cy - radius));
        var y1 = Math.Min(bgr.Height - 1, (int)(cy + radius));
        var count = 0;
        double sumX = 0, sumY = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy > radiusSq || cyan.At<byte>(y, x) == 0)
                {
                    continue;
                }
                count++;
                sumX += x;
                sumY += y;
            }
        }
        if (count < _cfg.MinPlayerMarkerPixels)
        {
            return false;
        }
        var mx = sumX / count;
        var my = sumY / count;
        offset = Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy));
        return offset <= _cfg.MaxPlayerMarkerOffset;
    }

    private bool IsValidMinimap(Mat gray, out double grayStd, out double laplacianVar)
    {
        Cv2.MeanStdDev(gray, out _, out var stddev);
        grayStd = stddev.Val0;
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var lapStd);
        laplacianVar = lapStd.Val0 * lapStd.Val0;
        return grayStd >= _cfg.MinMapGrayStd && laplacianVar >= _cfg.MinMapLaplacianVar;
    }


    /// <summary>
    /// Samples the minimap marker at a much higher rate than the map matching
    /// and returns one observation per sampled frame.
    ///
    /// This runs as a separate pass over the video because the wing glyph on
    /// the minimap flaps: its measured aspect ratio changes substantially
    /// between frames. At the 2 fps used for map matching, the samples miss
    /// the elongated pose most of the time (measured: F1 drops from 0.99 at
    /// 10 fps to 0.74 at 2 fps), so the marker has to be watched at video rate.
    /// </summary>
    public List<GlideMarkerObservation> SampleMarkers(MinimapCircle circle, int sampleFps, CancellationToken ct)
    {        var observations = new List<GlideMarkerObservation>();
        if (sampleFps <= 0)
        {
            return observations;
        }
        using var capture = new VideoCapture(_cfg.VideoPath);
        if (!capture.IsOpened())
        {
            return observations;
        }
        var fps = capture.Get(VideoCaptureProperties.Fps);
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
        {
            fps = 30;
        }
        var totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        var startFrame = (int)Math.Max(0, _cfg.StartTimeSec * fps);
        var endFrame = _cfg.EndTimeSec > 0 ? (int)Math.Min(totalFrames, _cfg.EndTimeSec * fps) : totalFrames;
        if (endFrame <= startFrame)
        {
            endFrame = totalFrames;
        }
        if (startFrame > 0)
        {
            capture.Set(VideoCaptureProperties.PosFrames, startFrame);
        }
        var step = Math.Max(1, (int)Math.Round(fps / sampleFps));
        using var frame = new Mat();
        var index = startFrame;
        while (index < endFrame && !ct.IsCancellationRequested)
        {
            if ((index - startFrame) % step == 0)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    break;
                }
                using var minimap = MinimapLocator.ExtractMinimap(frame, circle.CenterX, circle.CenterY, circle.Radius, _cfg.CropRatio);
                var obs = ObserveMinimapMarker(minimap);
                obs.TimeSec = index / fps;
                observations.Add(obs);
            }
            else
            {
                if (!capture.Grab())
                {
                    break;
                }
            }
            index++;
        }
        return observations;
    }

    /// <summary>
    /// Scans the video for the deployed wind-wing with the trained YOLO model.
    ///
    /// The video decode dominates the cost (about 1.4 ms per 1920x1080 H.264
    /// frame on the development machine), so the frames are read once and only
    /// every N-th frame is pushed through the model. At 3 fps a 396 s video
    /// costs about 5 minutes, and a glide lasting seconds is still resolved to
    /// roughly 0.3 s.
    /// </summary>
    /// <returns>The detected glide windows, in seconds.</returns>
    public List<GlideSegment> ScanForGlides(GlideYoloDetector detector, int sampleFps,
        Action<string>? log, CancellationToken ct,
        PauseToken? pause = null,
        IProgress<ScanTick>? progress = null)
    {
        var segments = new List<GlideSegment>();
        using var capture = new VideoCapture(_cfg.VideoPath);
        if (!capture.IsOpened())
        {
            log?.Invoke($"无法打开视频: {_cfg.VideoPath}");
            return segments;
        }
        var fps = capture.Get(VideoCaptureProperties.Fps);
        if (fps <= 0 || double.IsNaN(fps))
        {
            fps = 30;
        }
        var step = Math.Max(1, (int)Math.Round(fps / Math.Max(0.1, sampleFps)));
        var startFrame = (int)Math.Max(0, _cfg.StartTimeSec * fps);
        var endFrame = _cfg.EndTimeSec > 0
            ? (int)Math.Min(capture.Get(VideoCaptureProperties.FrameCount), _cfg.EndTimeSec * fps)
            : (int)capture.Get(VideoCaptureProperties.FrameCount);
        if (startFrame > 0)
        {
            capture.Set(VideoCaptureProperties.PosFrames, startFrame);
        }

        var hits = new List<(double Time, float Score)>();
        using var frame = new Mat();
        var index = startFrame;
        var sw = Stopwatch.StartNew();
        var scanned = 0;
        var totalSec = endFrame / fps;

        while (index < endFrame && !ct.IsCancellationRequested)
        {
            pause?.Wait(ct);
            if (!capture.Read(frame) || frame.Empty())
            {
                break;
            }
            if ((index - startFrame) % step == 0)
            {
                if (detector.Detect(frame, out var score))
                {
                    hits.Add((index / fps, score));
                }
                scanned++;
                if (scanned % 300 == 0)
                {
                    log?.Invoke($"  滑翔扫描 {index / fps:F0}s / {endFrame / fps:F0}s ...");
                }
                progress?.Report(new ScanTick(
                    index / fps, totalSec, hits.Count, 0, sw.Elapsed.TotalSeconds));
            }
            index++;
        }

        log?.Invoke($"滑翔扫描完成，用时 {sw.Elapsed.TotalSeconds:F1}s，检测 {hits.Count} 帧");

        // Group the contiguous detections into windows. The gap tolerance is
        // tied to the sampling rate: with 3 fps sampling a 1.0 s gap is one
        // and a bit missed samples, which happens while the wing is
        // re-adjusting.
        var maxGap = Math.Max(1.0, 1.5 / Math.Max(0.1, sampleFps) * 3);
        var i = 0;
        while (i < hits.Count)
        {
            var j = i;
            while (j + 1 < hits.Count && hits[j + 1].Time - hits[j].Time <= maxGap)
            {
                j++;
            }
            var window = hits.Skip(i).Take(j - i + 1).ToList();
            segments.Add(new GlideSegment
            {
                StartSec = window[0].Time,
                EndSec = window[^1].Time,
                Confidence = window.Max(h => h.Score),
                Reason = "YOLO 风之翼检测"
            });
            i = j + 1;
        }
        return segments;
    }

    /// <summary>
    /// Observes the cyan marker on the minimap crop and measures how elongated
    /// it is.
    ///
    /// While the character glides, the minimap draws the WIND-WING glyph
    /// instead of the ordinary character marker. The wing glyph is a long,
    /// thin, bilateral shape, so its length/width ratio is far higher than
    /// normal. The ratio is scale- and rotation-invariant, which is essential
    /// because the wing flaps and the character turns constantly.
    ///
    /// The HSV window is deliberately wider than the one BGI uses for the
    /// player marker: the strict window measures the small solid character
    /// icon, which does NOT change shape when the glider opens, while the
    /// loose window picks up the wing glyph that does.
    /// </summary>
    public static GlideMarkerObservation ObserveMinimapMarker(Mat minimapBgr)
    {
        var result = new GlideMarkerObservation { Valid = false };
        using var hsv = new Mat();
        Cv2.CvtColor(minimapBgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(80, 120, 120), new Scalar(100, 255, 255), mask);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        if (count < 2)
        {
            return result;
        }

        // The largest cyan blob is the marker of interest.
        var bestIndex = -1;
        var bestArea = 0;
        for (var i = 1; i < count; i++)
        {
            var area = stats.At<int>(i, 4);
            if (area > bestArea)
            {
                bestArea = area;
                bestIndex = i;
            }
        }
        if (bestIndex < 0 || bestArea < 30)
        {
            return result;
        }

        using var comp = new Mat();
        Cv2.Compare(labels, bestIndex, comp, CmpType.EQ);

        var pts = new List<Point2f>();
        for (var r = 0; r < comp.Rows; r++)
        {
            for (var c = 0; c < comp.Cols; c++)
            {
                if (comp.At<byte>(r, c) != 0)
                {
                    pts.Add(new Point2f(c, r));
                }
            }
        }
        if (pts.Count < 3)
        {
            return result;
        }

        double mx = 0, my = 0;
        foreach (var p in pts)
        {
            mx += p.X;
            my += p.Y;
        }
        mx /= pts.Count;
        my /= pts.Count;

        // principal axis of the blob
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts)
        {
            var dx = p.X - mx;
            var dy = p.Y - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }
        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var ax = Math.Cos(theta);
        var ay = Math.Sin(theta);
        var px = -ay;
        var py = ax;

        var minProj = double.MaxValue;
        var maxProj = double.MinValue;
        var perps = new List<double>();
        foreach (var p in pts)
        {
            var dx = p.X - mx;
            var dy = p.Y - my;
            var proj = dx * ax + dy * ay;
            minProj = Math.Min(minProj, proj);
            maxProj = Math.Max(maxProj, proj);
            perps.Add(Math.Abs(dx * px + dy * py));
        }
        var length = maxProj - minProj;
        perps.Sort();
        var width = perps.Count > 0 ? perps[perps.Count * 9 / 10] : 0;

        var cx = minimapBgr.Width / 2.0;
        var cy = minimapBgr.Height / 2.0;
        result.AspectRatio = width > 0.5 ? length / width : 0;
        result.CenterX = mx;
        result.CenterY = my;
        result.Area = pts.Count;
        result.DistanceFromCenter = Math.Sqrt((mx - cx) * (mx - cx) + (my - cy) * (my - cy));
        result.Valid = true;
        return result;
    }
}
