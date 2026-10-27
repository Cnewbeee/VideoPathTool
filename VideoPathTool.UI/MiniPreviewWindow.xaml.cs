using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace VideoPathTool.UI;

/// <summary>
/// A small always-on-top window that shows the source video at the position the
/// recorder has reached, so the guide can be watched while a run is going.
///
/// It owns its own <see cref="VideoCapture"/>: the recorder decodes the original
/// file sequentially on its own thread, and the editor may be previewing the
/// proxy at the same time, so sharing one decoder is not an option.
/// </summary>
public partial class MiniPreviewWindow : Window
{
    private readonly string _path;
    private VideoCapture? _capture;
    private double _fps = 30;
    private double _lastPositionSec = -1;
    private double _pendingSec = -1;
    private int _busy;

    public MiniPreviewWindow(string videoPath)
    {
        InitializeComponent();
        _path = videoPath;

        // Default position: bottom right, leaving room for the taskbar.
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;

        Loaded += (_, _) => OpenCapture();
        Closed += (_, _) => DisposeCapture();
    }

    private void OnBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OpenCapture()
    {
        try
        {
            _capture = new VideoCapture(_path);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                _capture = null;
                TxtStatus.Text = "无法打开视频";
                return;
            }
            var fps = _capture.Get(VideoCaptureProperties.Fps);
            if (fps > 1 && fps < 240)
            {
                _fps = fps;
            }
            TxtEmpty.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "打开失败: " + ex.Message;
        }
    }

    private void DisposeCapture()
    {
        try
        {
            _capture?.Dispose();
        }
        catch
        {
            // Closing; nothing useful to do.
        }
        _capture = null;
    }

    /// <summary>
    /// Called from the recorder's progress tick. Only seeks when the position
    /// moved enough to matter - a 1080p seek costs a keyframe, and ticks arrive
    /// about once a second.
    /// </summary>
    public void UpdatePosition(double seconds, string? status)
    {
        if (!string.IsNullOrEmpty(status))
        {
            TxtStatus.Text = status;
        }
        TxtClock.Text = seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";

        if (_capture is null || Math.Abs(seconds - _lastPositionSec) < 0.6)
        {
            return;
        }
        // One grab at a time; a dropped update is invisible at this cadence.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        _pendingSec = seconds;
        var target = seconds;
        Task.Run(() =>
        {
            Mat? frame = null;
            try
            {
                var capture = _capture;
                if (capture is null)
                {
                    return;
                }
                var index = Math.Max(0, (int)Math.Round(target * _fps));
                capture.Set(VideoCaptureProperties.PosFrames, index);
                frame = new Mat();
                if (!capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    return;
                }
                var source = ToBitmap(frame);
                frame.Dispose();
                frame = null;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    FrameImage.Source = source;
                    _lastPositionSec = target;
                }));
            }
            catch
            {
                // A failed grab only costs one frame of the preview.
            }
            finally
            {
                frame?.Dispose();
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private static BitmapSource ToBitmap(Mat mat)
    {
        using var converted = mat.Channels() == 3
            ? mat.CvtColor(ColorConversionCodes.BGR2RGB)
            : mat.CvtColor(ColorConversionCodes.GRAY2RGB);
        var bitmap = BitmapSource.Create(
            converted.Width, converted.Height, 96, 96,
            PixelFormats.Rgb24, null,
            converted.Data, converted.Width * converted.Height * 3,
            converted.Width * 3);
        bitmap.Freeze();
        return bitmap;
    }
}
