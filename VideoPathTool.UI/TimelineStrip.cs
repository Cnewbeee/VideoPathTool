using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoPathTool.UI;

/// <summary>
/// A compact strip that draws where the annotations are on the video, so the
/// user can check at a glance whether anything is still unmarked.
///
/// Two lanes:
///   top    glide windows (blue = detected, amber = hand marked, grey = disabled)
///   bottom point actions (green ticks)
///
/// Click or drag anywhere on it to seek. This is deliberately a plain
/// FrameworkElement with hand-drawn geometry instead of a chart control: it only
/// needs rectangles and lines, and there is no dependency to carry.
/// </summary>
public sealed class TimelineStrip : FrameworkElement
{
    public readonly record struct StripGlide(double StartSec, double EndSec, bool Enabled, bool Manual);

    public readonly record struct StripAction(double TimeSec, bool Enabled, string Label);

    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)));
    private static readonly Brush DetectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush ManualBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly Brush ActionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
    private static readonly Brush DisabledBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)), 1));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF3)), 1));
    private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), 2));

    private readonly List<StripGlide> _glides = new();
    private readonly List<StripAction> _actions = new();
    private double _duration = 1;
    private double _playhead;

    public TimelineStrip()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        Height = 34;
    }

    /// <summary>Raised when the user clicks or drags the strip.</summary>
    public event EventHandler<double>? SeekRequested;

    public void SetDuration(double seconds)
    {
        _duration = Math.Max(0.1, seconds);
        InvalidateVisual();
    }

    public void SetMarks(IEnumerable<StripGlide> glides, IEnumerable<StripAction> actions)
    {
        _glides.Clear();
        _glides.AddRange(glides);
        _actions.Clear();
        _actions.AddRange(actions);
        InvalidateVisual();
    }

    public void SetPlayhead(double seconds)
    {
        if (Math.Abs(_playhead - seconds) < 0.002)
        {
            return;
        }
        _playhead = seconds;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 34);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        // Painted background also makes the whole strip hit-testable.
        dc.DrawRectangle(Background, BorderPen, new Rect(0, 0, w, h));

        var barTop = 5.0;
        var barH = Math.Max(6.0, h * 0.38);

        // Time gridlines, kept sparse enough to stay readable.
        var step = 30.0;
        while (step / _duration * w < 42)
        {
            step *= step < 60 ? 2 : 2;
        }
        for (var t = step; t < _duration; t += step)
        {
            var x = t / _duration * w;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
        }

        foreach (var g in _glides)
        {
            var x1 = g.StartSec / _duration * w;
            var x2 = g.EndSec / _duration * w;
            var rect = new Rect(x1, barTop, Math.Max(2.0, x2 - x1), barH);
            var brush = !g.Enabled ? DisabledBrush : g.Manual ? ManualBrush : DetectedBrush;
            dc.DrawRectangle(brush, null, rect);
        }

        var tickTop = barTop + barH + 3;
        foreach (var a in _actions)
        {
            var x = a.TimeSec / _duration * w;
            var brush = a.Enabled ? ActionBrush : DisabledBrush;
            dc.DrawRectangle(brush, null, new Rect(x - 1.5, tickTop, 3, Math.Max(4.0, h - tickTop - 3)));
        }

        var px = _playhead / _duration * w;
        dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        Emit(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            Emit(x);
            return;
        }
        ToolTip = Describe(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    private void Emit(double x)
    {
        var fraction = Math.Clamp(x / Math.Max(1.0, ActualWidth), 0.0, 1.0);
        SeekRequested?.Invoke(this, fraction * _duration);
    }

    private string Describe(double x)
    {
        var t = Math.Clamp(x / Math.Max(1.0, ActualWidth), 0.0, 1.0) * _duration;
        foreach (var g in _glides)
        {
            if (t >= g.StartSec && t <= g.EndSec)
            {
                var kind = g.Manual ? "手工标记" : "检测";
                var state = g.Enabled ? "" : "（已禁用）";
                return string.Format(CultureInfo.CurrentCulture,
                    "{0:F1}–{1:F1}s 滑翔 · {2}{3}", g.StartSec, g.EndSec, kind, state);
            }
        }
        foreach (var a in _actions)
        {
            if (Math.Abs(a.TimeSec - t) <= Math.Max(0.4, _duration / Math.Max(1.0, ActualWidth) * 6))
            {
                var state = a.Enabled ? "" : "（已禁用）";
                return string.Format(CultureInfo.CurrentCulture,
                    "{0:F1}s 动作：{1}{2}", a.TimeSec, a.Label, state);
            }
        }
        return string.Format(CultureInfo.CurrentCulture, "{0:F1}s · 无标注", t);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }
        return freezable;
    }
}
