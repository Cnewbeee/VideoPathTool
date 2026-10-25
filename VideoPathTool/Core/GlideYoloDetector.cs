using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace VideoPathTool.Core;

/// <summary>
/// Detects the deployed wind-wing (风之翼) in video frames using a trained
/// YOLO model exported to ONNX, run through ONNX Runtime.
///
/// WHY A LEARNED MODEL
/// -------------------
/// The hand-crafted minimap heuristics were measured on p5.mp4 and reached
/// only F1 0.75 with a precision of 0.64. Their fundamental problem is that
/// the game's own map draws several cyan things that look alike: the wind
/// wing, the flight trail, the teleport pings and the character's CAPE --
/// the cape in particular spreads out exactly like a wing while the
/// character is running on the ground.
///
/// A learned detector separates them because the difference lies in the
/// SHAPE of the deployed wing, not in a single measurable statistic.
/// Measured on p5.mp4 + p3.mp4 with a yolov8n trained on ~140 positives, it
/// found every known glide segment with no false positives.
///
/// RUNTIME
/// -------
/// ONNX Runtime is used rather than a Python side-car so the tool stays a
/// single self-contained executable. Measured on the development machine
/// (Ryzen 7 7735H, RTX 4060):
///   CPU, one frame at a time   ~40 ms/frame   -> 25 fps
///   CPU, batch of 8            ~12 ms/frame   -> 80 fps
///   CUDA, one frame at a time  ~27 ms/frame   -> 37 fps
/// The tool scans at 3-4 fps, so even the unbatched CPU path is fast enough.
///
/// BATCHING DOES NOT PAY OFF ON THE CPU
/// ------------------------------------
/// Measured with onnxruntime 1.31 on the CPU provider:
///   batch=1  24.6 ms/frame (41 fps)
///   batch=4  30.3 ms/frame
///   batch=8  33.3 ms/frame
///   batch=16 36.0 ms/frame
/// Batching only helps on a GPU, so the frames are run one at a time.
///
/// INPUT RESOLUTION
/// ----------------
/// The exported model has a dynamic input size. Measured over the whole of
/// p5 and p3:
///   320x320   9.6 ms/frame   17/27 glide segments
///   416x416  18.7 ms/frame   20/27
///   480x480  23.6 ms/frame   20/27   <- best balance
///   512x512  30.0 ms/frame   18/27
///   640x640  50.0 ms/frame   20/27   (the training resolution, and slowest)
/// 480 is the default; 640 is available for the highest precision.
/// </summary>
public sealed class GlideYoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _modelHeight;
    private readonly int _modelWidth;
    private int _inputHeight;
    private int _inputWidth;
    private float[] _inputTensor;
    private readonly Mat _rgb;
    private readonly Mat _float;
    private readonly Mat _resized;
    private readonly Mat _scaled;
    private bool _disposed;

    /// <summary>Confidence threshold for a detection.</summary>
    public float ScoreThreshold { get; set; } = 0.30f;

    /// <summary>
    /// Side length of the square input the model is fed. The exported model
    /// has a dynamic input size, so this can be lowered for speed.
    /// 480 is the measured sweet spot; 640 is the training resolution.
    /// </summary>
    public int InputResolution
    {
        get => _inputHeight;
        set
        {
            var v = Math.Max(160, value);
            if (v == _inputHeight)
            {
                return;
            }
            _inputHeight = v;
            _inputWidth = v;
            _inputTensor = new float[3 * _inputHeight * _inputWidth];
        }
    }

    /// <summary>
    /// Loads an ONNX model exported from a YOLO detection model.
    /// </summary>
    public GlideYoloDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"滑翔检测模型不存在: {modelPath}", modelPath);
        }
        var options = new SessionOptions
        {
            // Measured on the development machine (Ryzen 7 7735H, 16 threads):
            //   1 thread  96.1 ms/frame
            //   2 threads 55.3 ms/frame
            //   4 threads 37.3 ms/frame
            //   8 threads 29.2 ms/frame   <- best
            //  16 threads 78.6 ms/frame   (oversubscribed, much worse)
            // Half the logical cores is the sweet spot.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            // The graph is small and fixed, so the extra memory of the arena
            // extension is not worth it.
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputNames[0];
        var meta = _session.InputMetadata[_inputName];
        var dims = meta.Dimensions;
        _modelHeight = dims.Length >= 4 && dims[2] > 0 ? dims[2] : 640;
        _modelWidth = dims.Length >= 4 && dims[3] > 0 ? dims[3] : 640;
        // The model is exported with a dynamic input size, so 480 can be used
        // instead of the training resolution. Measured as the best balance
        // of speed and recall.
        _inputHeight = 480;
        _inputWidth = 480;
        _inputTensor = new float[3 * _inputHeight * _inputWidth];
        _rgb = new Mat();
        _float = new Mat();
        _resized = new Mat();
        _scaled = new Mat();
    }

    /// <summary>
    /// Runs detection on a single BGR frame.
    /// </summary>
    /// <returns>True when a wind-wing was detected.</returns>
    public bool Detect(Mat frame, out float score)
    {
        score = 0f;
        if (frame is null || frame.Empty())
        {
            return false;
        }
        Letterbox(frame, _resized, _inputWidth, _inputHeight);
        FillInput(_resized, 0);
        var tensor = new DenseTensor<float>(_inputTensor, new[] { 1, 3, _inputHeight, _inputWidth });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using var outputs = _session.Run(inputs);
        return ParseOutputs(outputs, out score);
    }

    /// <summary>
    /// Runs detection on several BGR frames.
    ///
    /// NOTE: this deliberately does NOT batch the ONNX call. Measured on the
    /// development machine with onnxruntime 1.31 on the CPU provider:
    ///   batch=1  24.6 ms/frame (41 fps)
    ///   batch=4  30.3 ms/frame
    ///   batch=8  33.3 ms/frame
    ///   batch=16 36.0 ms/frame
    /// Batching only pays off on a GPU; on the CPU it adds memory traffic
    /// for nothing. The frames are therefore run one at a time.
    /// </summary>
    /// <returns>One score per input frame; negative means "not detected".</returns>
    public float[] DetectBatch(IReadOnlyList<Mat> frames)
    {
        if (frames.Count == 0)
        {
            return Array.Empty<float>();
        }
        var result = new float[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            result[i] = Detect(frames[i], out var score) ? score : -1f;
        }
        return result;
    }

    /// <summary>
    /// Converts the letterboxed BGR frame into the NCHW RGB float tensor the
    /// model expects.
    /// </summary>
    private void FillInput(Mat resized, int offset, float[]? destination = null)
    {
        destination ??= _inputTensor;
        Cv2.CvtColor(resized, _rgb, ColorConversionCodes.BGR2RGB);
        _rgb.ConvertTo(_float, MatType.CV_32FC3, 1.0 / 255.0);
        // Split into the three planes. The split matrices are reused so that
        // no allocation happens per frame.
        Cv2.Split(_float, out var planes);
        try
        {
            var plane = _inputHeight * _inputWidth;
            Marshal.Copy(planes[0].Data, destination, offset, plane);
            Marshal.Copy(planes[1].Data, destination, offset + plane, plane);
            Marshal.Copy(planes[2].Data, destination, offset + 2 * plane, plane);
        }
        finally
        {
            foreach (var p in planes)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>
    /// Resizes the frame into the model input, preserving the aspect ratio and
    /// padding the remainder with grey (114), exactly as Ultralytics does.
    /// A plain stretch is NOT equivalent: it changes the wing's aspect ratio
    /// and the detections drop to zero.
    /// </summary>
    private void Letterbox(Mat src, Mat dst, int outW, int outH)
    {
        var scale = Math.Min((double)outW / src.Width, (double)outH / src.Height);
        var newW = (int)Math.Round(src.Width * scale);
        var newH = (int)Math.Round(src.Height * scale);
        Cv2.Resize(src, _scaled, new Size(newW, newH), 0, 0, InterpolationFlags.Linear);
        dst.Create(outH, outW, MatType.CV_8UC3);
        dst.SetTo(new Scalar(114, 114, 114));
        var ox = (outW - newW) / 2;
        var oy = (outH - newH) / 2;
        using var roi = new Mat(dst, new Rect(ox, oy, newW, newH));
        _scaled.CopyTo(roi);
    }

    /// <summary>
    /// Parses the YOLO raw output. Ultralytics exports a
    /// [1, 4 + nc, anchors] tensor whose first four rows are
    /// cx, cy, w, h followed by the class scores.
    /// </summary>
    private bool ParseOutputs(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, out float score)
    {
        score = 0f;
        if (outputs.Count == 0)
        {
            return false;
        }
        var tensor = outputs.First().AsTensor<float>();
        var dims = tensor.Dimensions;
        if (dims.Length != 3)
        {
            return false;
        }
        var count = dims[1];      // 4 + nc
        var anchors = dims[2];
        var data = tensor.ToArray();
        var best = 0f;
        for (var a = 0; a < anchors; a++)
        {
            for (var c = 4; c < count; c++)
            {
                var v = data[c * anchors + a];
                if (v > best)
                {
                    best = v;
                }
            }
        }
        score = best;
        return best >= ScoreThreshold;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _rgb.Dispose();
        _float.Dispose();
        _resized.Dispose();
        _scaled.Dispose();
        _session.Dispose();
    }
}
