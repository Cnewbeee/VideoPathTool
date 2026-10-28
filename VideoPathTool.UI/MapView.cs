using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoPathTool.UI;

/// <summary>
/// Draws the route on top of the Teyvat map image.
///
/// Everything is laid out in <b>map pixel space</b> (the 5632x4864 Teyvat_0_256
/// image, the same one BetterGI's map layers use) and a single transform scales
/// it to the viewport, so pan and zoom never touch the point data.
///
/// Coordinates come from BetterGI's convention: the map origin sits at
/// (4096, 3072) in that image, game +X goes left and +Y goes up at a quarter of
/// a pixel per unit.
/// </summary>
public sealed class MapView : FrameworkElement
{
    /// <summary>Size of the Teyvat_0_256 map image in pixels.</summary>
    public const double MapWidth = 5632;
    public const double MapHeight = 4864;

    private const double OriginX = 4096;
    private const double OriginY = 3072;
    private const double UnitsPerPixel = 4;

    private BitmapSource? _map;
    private double _scale = 0.15;
    private Vector _offset;
    private bool _panning;
    private Point _dragStart;
    private Vector _dragOrigin;

    /// <summary>Index of the marker being dragged, or -1.</summary>
    private int _dragIndex = -1;
    private bool _dragMoved;

    public MapView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Cursor = Cursors.Hand;
    }

    /// <summary>Waypoints in map pixel space, in route order.</summary>
    public IReadOnlyList<Point> Points { get; set; } = Array.Empty<Point>();

    /// <summary>Per-point flags; true draws the segment leading there as a teleport.</summary>
    public IReadOnlyList<bool> IsTeleport { get; set; } = Array.Empty<bool>();

    public int SelectedIndex { get; set; } = -1;

    /// <summary>Current zoom factor, shown in the toolbar so the scale is obvious.</summary>
    public double ZoomFactor => _scale;

    /// <summary>Raised when a marker is clicked; -1 when empty space is clicked.</summary>
    public event Action<int>? PointClicked;

    /// <summary>Raised on double click with the clicked position in map pixels.</summary>
    public event Action<Point>? MapDoubleClicked;

    /// <summary>Raised while a marker is dragged, with its new map position.</summary>
    public event Action<int, Point>? PointDragged;

    /// <summary>Raised when a marker drag finishes.</summary>
    public event Action<int>? PointDragCompleted;

    /// <summary>
    /// Raised on right click over the map: the marker index (-1 when empty space)
    /// and the clicked position in map pixels, so the host can insert a point there.
    /// </summary>
    public event Action<int, Point>? MarkerRightClicked;

    /// <summary>Converts a BetterGI game coordinate to a position in the map image.</summary>
    public static Point ToMapPixel(double gameX, double gameY) =>
        new(OriginX - gameX / UnitsPerPixel, OriginY - gameY / UnitsPerPixel);

    /// <summary>Inverse of <see cref="ToMapPixel"/>, used when placing a point.</summary>
    public static (double X, double Y) ToGameCoord(Point mapPixel) =>
        ((OriginX - mapPixel.X) * UnitsPerPixel, (OriginY - mapPixel.Y) * UnitsPerPixel);

    public void LoadMap(BitmapSource map)
    {
        _map = map;
        InvalidateVisual();
    }

    /// <summary>Zooms so the whole route (plus a margin) fills the view.</summary>
    public void FitToRoute()
    {
        if (Points.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var minX = Points.Min(p => p.X);
        var maxX = Points.Max(p => p.X);
        var minY = Points.Min(p => p.Y);
        var maxY = Points.Max(p => p.Y);
        var pad = Math.Max(120, Math.Max(maxX - minX, maxY - minY) * 0.12);

        var w = Math.Max(1, (maxX - minX) + pad * 2);
        var h = Math.Max(1, (maxY - minY) + pad * 2);
        _scale = Math.Min(ActualWidth / w, ActualHeight / h);
        // 底图整个提瓦特只有 5632 像素宽（1 像素约 4 游戏单位），一条几百单位的
        // 路线本来就只有几十像素。放到 4 倍以上只会把底图糊成一片，看起来像"误差"，
        // 所以自动适应止步于此，需要更细可以手动放大或用更高分辨率的底图。
        _scale = Math.Clamp(_scale, 0.02, 4);

        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        _offset = new Vector(ActualWidth / 2 - cx * _scale, ActualHeight / 2 - cy * _scale);
        InvalidateVisual();
    }

    public void ZoomBy(double factor, Point? around = null)
    {
        var anchor = around ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var before = ToMapSpace(anchor);
        _scale = Math.Clamp(_scale * factor, 0.02, 12);

        // Keep the point under the cursor fixed while the scale changes.
        _offset = new Vector(anchor.X - before.X * _scale, anchor.Y - before.Y * _scale);
        InvalidateVisual();
    }

    private Point ToMapSpace(Point viewport) =>
        new((viewport.X - _offset.X) / _scale, (viewport.Y - _offset.Y) / _scale);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        // 放大超过 2.5 倍时改用最近邻：宁可看到方块像素，也不要一团模糊。
        RenderOptions.SetBitmapScalingMode(this,
            _scale > 2.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        var transform = new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        dc.PushTransform(transform);

        if (_map is not null)
        {
            dc.DrawImage(_map, new Rect(0, 0, MapWidth, MapHeight));
        }

        // Route first, so markers sit on top of the line.
        if (Points.Count > 1)
        {
            var normal = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), 3 / _scale);
            var teleport = new Pen(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)), 3 / _scale);
            for (var i = 1; i < Points.Count; i++)
            {
                var isTp = i < IsTeleport.Count && IsTeleport[i];
                dc.DrawLine(isTp ? teleport : normal, Points[i - 1], Points[i]);
            }
        }

        var radius = 5 / _scale;
        for (var i = 0; i < Points.Count; i++)
        {
            var selected = i == SelectedIndex;
            var fill = selected
                ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                : new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            var pen = new Pen(Brushes.White, (selected ? 2.5 : 1.2) / _scale);
            dc.DrawEllipse(fill, pen, Points[i], selected ? radius * 1.8 : radius, selected ? radius * 1.8 : radius);

            if (selected)
            {
                // 十字准星：路线压在低分辨率底图上时，唯一能精确读位置的东西。
                var cross = new Pen(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), 1.5 / _scale);
                var arm = 14 / _scale;
                dc.DrawLine(cross, new Point(Points[i].X - arm, Points[i].Y), new Point(Points[i].X + arm, Points[i].Y));
                dc.DrawLine(cross, new Point(Points[i].X, Points[i].Y - arm), new Point(Points[i].X, Points[i].Y + arm));
            }
        }

        dc.Pop();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            MapDoubleClicked?.Invoke(ToMapSpace(e.GetPosition(this)));
            e.Handled = true;
            return;
        }

        var hit = HitTestPoint(ToMapSpace(e.GetPosition(this)));
        if (hit >= 0)
        {
            // 按住标记 = 拖动这个点位；松手才落点。
            _dragIndex = hit;
            _dragMoved = false;
            PointClicked?.Invoke(hit);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        // Empty space: start panning.
        _panning = true;
        _dragStart = e.GetPosition(this);
        _dragOrigin = _offset;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragIndex >= 0)
        {
            _dragMoved = true;
            PointDragged?.Invoke(_dragIndex, ToMapSpace(e.GetPosition(this)));
            return;
        }
        if (!_panning)
        {
            return;
        }
        var now = e.GetPosition(this);
        _offset = _dragOrigin + (now - _dragStart);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragIndex >= 0)
        {
            var index = _dragIndex;
            var moved = _dragMoved;
            _dragIndex = -1;
            _dragMoved = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            if (moved)
            {
                PointDragCompleted?.Invoke(index);
            }
            e.Handled = true;
            return;
        }

        if (!_panning)
        {
            return;
        }
        _panning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;

        // A press that never moved is a click on nothing: clear the selection.
        var now = e.GetPosition(this);
        if ((now - _dragStart).Length < 3)
        {
            PointClicked?.Invoke(-1);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        // 只吞掉事件：菜单留到抬起时再开。按下时就 IsOpen = true 的话，
        // 紧接着的抬起会被当成"点到了别处"，菜单一闪就没了。
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var mapPoint = ToMapSpace(e.GetPosition(this));
        MarkerRightClicked?.Invoke(HitTestPoint(mapPoint), mapPoint);
        e.Handled = true;
    }

    /// <summary>Index of the marker nearest to a map-space position, or -1.</summary>
    private int HitTestPoint(Point mapPoint)
    {
        var tolerance = 10 / _scale;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Points.Count; i++)
        {
            var d = (Points[i] - mapPoint).Length;
            if (d < tolerance && d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }
}
