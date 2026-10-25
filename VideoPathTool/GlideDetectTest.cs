using System.Diagnostics;
using OpenCvSharp;
using VideoPathTool.Core;

namespace VideoPathTool;

/// <summary>
/// Standalone check of the ONNX glider detector: run it over a video at the
/// tool's working rate and print the detected glide windows.
/// </summary>
public static class GlideDetectTest
{
    public static void Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: glide-test <model.onnx> <video> [fps] [conf]");
            return;
        }
        var modelPath = args[0];
        var videoPath = args[1];
        var fps = args.Length > 2 ? double.Parse(args[2]) : 3.0;
        var conf = args.Length > 3 ? float.Parse(args[3]) : 0.30f;

        using var detector = new GlideYoloDetector(modelPath) { ScoreThreshold = conf };
        using var capture = new VideoCapture(videoPath);
        if (!capture.IsOpened())
        {
            Console.WriteLine($"无法打开视频: {videoPath}");
            return;
        }
        var videoFps = capture.Get(VideoCaptureProperties.Fps);
        if (videoFps <= 0)
        {
            videoFps = 30;
        }
        var step = Math.Max(1, (int)Math.Round(videoFps / fps));
        Console.WriteLine($"模型: {modelPath}");
        Console.WriteLine($"视频: {videoPath}  {videoFps:F1}fps  采样步长 {step}  阈值 {conf}");

        var sw = Stopwatch.StartNew();
        var hits = new List<(double Time, float Score)>();
        var index = 0;
        var scanned = 0;
        using var frame = new Mat();
        var batch = new List<Mat>();
        var batchTimes = new List<double>();
        const int batchSize = 8;

        void Flush()
        {
            if (batch.Count == 0)
            {
                return;
            }
            var scores = detector.DetectBatch(batch);
            for (var i = 0; i < batch.Count; i++)
            {
                if (scores[i] >= 0)
                {
                    hits.Add((batchTimes[i], scores[i]));
                }
            }
            batch.Clear();
            batchTimes.Clear();
        }

        while (true)
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                break;
            }
            if (index % step == 0)
            {
                batch.Add(frame.Clone());
                batchTimes.Add(index / videoFps);
                scanned++;
                if (batch.Count >= batchSize)
                {
                    Flush();
                }
            }
            index++;
        }
        Flush();
        var elapsed = sw.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"扫描 {scanned} 帧，用时 {elapsed:F1}s ({scanned / elapsed:F1} fps，" +
                          $"{elapsed / scanned * 1000:F1} ms/帧)");
        Console.WriteLine($"检出 {hits.Count} 帧");

        // group into windows
        var windows = new List<(double Start, double End, float Max)>();
        var i = 0;
        while (i < hits.Count)
        {
            var j = i;
            while (j + 1 < hits.Count && hits[j + 1].Time - hits[j].Time <= 1.5)
            {
                j++;
            }
            var max = hits.Skip(i).Take(j - i + 1).Max(h => h.Score);
            windows.Add((hits[i].Time, hits[j].Time, max));
            i = j + 1;
        }
        Console.WriteLine();
        Console.WriteLine("=== 检出的滑翔段 ===");
        foreach (var (start, end, max) in windows)
        {
            Console.WriteLine($"  {start,7:F2}s -> {end,7:F2}s  最高置信度 {max:F3}");
        }
    }
}
