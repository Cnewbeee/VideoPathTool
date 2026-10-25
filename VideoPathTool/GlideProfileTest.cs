using System.Diagnostics;
using OpenCvSharp;
using VideoPathTool.Core;

namespace VideoPathTool;

/// <summary>
/// Split the glide scan into its two costs -- video decoding and inference --
/// so the bottleneck is known instead of guessed.
/// </summary>
public static class GlideProfileTest
{
    public static void Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: glide-profile <model.onnx> <video> [fps]");
            return;
        }
        var modelPath = args[0];
        var videoPath = args[1];
        var fps = args.Length > 2 ? double.Parse(args[2]) : 3.0;

        using var detector = new GlideYoloDetector(modelPath);
        using var capture = new VideoCapture(videoPath);
        var videoFps = capture.Get(VideoCaptureProperties.Fps);
        var step = Math.Max(1, (int)Math.Round(videoFps / fps));

        // 1. decode only (all frames, discard all but every step-th)
        var swDecode = Stopwatch.StartNew();
        using var frame = new Mat();
        var nDecoded = 0;
        var nKept = 0;
        while (capture.Read(frame) && !frame.Empty())
        {
            nDecoded++;
            if (nDecoded % step == 0) nKept++;
        }
        var decodeSec = swDecode.Elapsed.TotalSeconds;

        // 2. inference only (re-read the kept frames, no decode accounting)
        capture.Set(VideoCaptureProperties.PosFrames, 0);
        var swInfer = Stopwatch.StartNew();
        var nInfer = 0;
        double inferMs = 0;
        while (capture.Read(frame) && !frame.Empty())
        {
            nDecoded++;
            if (nInfer % step == 0)
            {
                var t0 = Stopwatch.StartNew();
                detector.Detect(frame, out _);
                inferMs += t0.Elapsed.TotalMilliseconds;
                nInfer++;
            }
        }
        var inferSec = swInfer.Elapsed.TotalSeconds;

        Console.WriteLine($"视频: {videoPath}  {videoFps:F0}fps  采样 {fps}fps");
        Console.WriteLine();
        Console.WriteLine($"  解码全部帧      : {decodeSec,7:F1} s   ({nDecoded} 帧, {decodeSec / nDecoded * 1000:F1} ms/帧)");
        Console.WriteLine($"  推理 {nInfer} 帧      : {inferMs / 1000,7:F1} s   ({inferMs / nInfer:F1} ms/帧)");
        Console.WriteLine($"  扫描总计        : {decodeSec + inferMs / 1000,7:F1} s");
        Console.WriteLine();
        Console.WriteLine($"  => 推理只占 {inferMs / 1000 / (decodeSec + inferMs / 1000) * 100:F0}%，其余全是解码");
    }
}
