using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CharacterConceptAI.Desktop.Components;

public partial class MaskPainterCanvas : UserControl
{
    private sealed class MaskStroke
    {
        public bool IsErase { get; init; }
        public double RelativeBrushRadius { get; init; } // relative to image pixel width
        public List<Point> NormalizedPoints { get; } = []; // normalized [0..1] in image space
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(MaskPainterCanvas),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty BrushSizeProperty = DependencyProperty.Register(
        nameof(BrushSize), typeof(double), typeof(MaskPainterCanvas),
        new PropertyMetadata(44.0, OnBrushSizeChanged));

    public static readonly DependencyProperty IsEraseModeProperty = DependencyProperty.Register(
        nameof(IsEraseMode), typeof(bool), typeof(MaskPainterCanvas),
        new PropertyMetadata(false, OnBrushSizeChanged));

    public static readonly DependencyProperty HasMaskStrokesProperty = DependencyProperty.Register(
        nameof(HasMaskStrokes), typeof(bool), typeof(MaskPainterCanvas),
        new PropertyMetadata(false));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double BrushSize
    {
        get => (double)GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public bool IsEraseMode
    {
        get => (bool)GetValue(IsEraseModeProperty);
        set => SetValue(IsEraseModeProperty, value);
    }

    public bool HasMaskStrokes
    {
        get => (bool)GetValue(HasMaskStrokesProperty);
        private set => SetValue(HasMaskStrokesProperty, value);
    }

    private static readonly SolidColorBrush FrozenPaintBrush = CreateFrozenBrush(Color.FromRgb(178, 203, 37));
    private static readonly SolidColorBrush FrozenEraseBrush = CreateFrozenBrush(Color.FromRgb(0, 0, 0));
    private static readonly SolidColorBrush FrozenCursorPaintStroke = CreateFrozenBrush(Color.FromRgb(178, 203, 37));
    private static readonly SolidColorBrush FrozenCursorPaintFill = CreateFrozenBrush(Color.FromArgb(45, 178, 203, 37));
    private static readonly SolidColorBrush FrozenCursorEraseStroke = CreateFrozenBrush(Color.FromRgb(248, 113, 113));
    private static readonly SolidColorBrush FrozenCursorEraseFill = CreateFrozenBrush(Color.FromArgb(40, 248, 113, 113));

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private readonly List<MaskStroke> _strokes = [];
    private MaskStroke? _activeStroke;
    private Polyline? _activePolyline;
    private Ellipse? _activeDot;
    private Rect _activeImageRect = Rect.Empty;
    private bool _isDrawing;

    public MaskPainterCanvas()
    {
        InitializeComponent();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MaskPainterCanvas painter)
            painter.ClearMask();
    }

    private static void OnBrushSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MaskPainterCanvas painter)
            painter.UpdateCursorVisual();
    }

    public void ClearMask()
    {
        _strokes.Clear();
        _activeStroke = null;
        _activePolyline = null;
        _activeDot = null;
        _isDrawing = false;
        HasMaskStrokes = false;
        RedrawMaskLayer();
    }

    public void UndoLastStroke()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        HasMaskStrokes = _strokes.Any(s => !s.IsErase);
        RedrawMaskLayer();
    }

    private Rect GetImageRectInStage()
    {
        if (Source is not BitmapSource bmp || StageBorder == null || StageBorder.ActualWidth <= 1 || StageBorder.ActualHeight <= 1)
            return Rect.Empty;

        var stageW = StageBorder.ActualWidth;
        var stageH = StageBorder.ActualHeight;
        var scale = Math.Min(stageW / bmp.PixelWidth, stageH / bmp.PixelHeight);
        var renderW = bmp.PixelWidth * scale;
        var renderH = bmp.PixelHeight * scale;
        var offsetX = (stageW - renderW) * 0.5;
        var offsetY = (stageH - renderH) * 0.5;
        return new Rect(offsetX, offsetY, renderW, renderH);
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawMaskLayer();

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var rect = GetImageRectInStage();
        if (rect.IsEmpty) return;

        var pos = e.GetPosition(StageBorder);
        if (!rect.Contains(pos)) return;

        _isDrawing = true;
        _activeImageRect = rect;
        StageBorder.CaptureMouse();

        var normPt = new Point((pos.X - rect.X) / rect.Width, (pos.Y - rect.Y) / rect.Height);
        var relRadius = (BrushSize * 0.5) / Math.Max(1.0, rect.Width);
        _activeStroke = new MaskStroke
        {
            IsErase = IsEraseMode,
            RelativeBrushRadius = relRadius
        };
        _activeStroke.NormalizedPoints.Add(normPt);
        _strokes.Add(_activeStroke);
        HasMaskStrokes = _strokes.Any(s => !s.IsErase);

        // O(1) incremental dot; upgrades to Polyline on first drag delta without clearing MaskLayer
        var diameter = Math.Max(4.0, relRadius * 2.0 * rect.Width);
        var brush = IsEraseMode ? FrozenEraseBrush : FrozenPaintBrush;
        _activePolyline = null;
        _activeDot = new Ellipse { Width = diameter, Height = diameter, Fill = brush };
        Canvas.SetLeft(_activeDot, pos.X - diameter * 0.5);
        Canvas.SetTop(_activeDot, pos.Y - diameter * 0.5);
        MaskLayer.Children.Add(_activeDot);

        UpdateCursorVisual();
        UpdateCursorPosition(pos);
        e.Handled = true;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(StageBorder);
        UpdateCursorPosition(pos);
        if (!_isDrawing || _activeStroke == null) return;

        var rect = _activeImageRect.IsEmpty ? GetImageRectInStage() : _activeImageRect;
        if (rect.IsEmpty) return;

        var clampedX = Math.Clamp(pos.X, rect.Left, rect.Right);
        var clampedY = Math.Clamp(pos.Y, rect.Top, rect.Bottom);
        var normPt = new Point((clampedX - rect.X) / rect.Width, (clampedY - rect.Y) / rect.Height);
        var last = _activeStroke.NormalizedPoints[^1];
        if ((normPt - last).Length < 0.0025) return;

        _activeStroke.NormalizedPoints.Add(normPt);
        if (_activePolyline == null)
        {
            if (_activeDot != null)
            {
                MaskLayer.Children.Remove(_activeDot);
                _activeDot = null;
            }
            var diameter = Math.Max(4.0, _activeStroke.RelativeBrushRadius * 2.0 * rect.Width);
            _activePolyline = new Polyline
            {
                Stroke = _activeStroke.IsErase ? FrozenEraseBrush : FrozenPaintBrush,
                StrokeThickness = diameter,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            var first = _activeStroke.NormalizedPoints[0];
            _activePolyline.Points.Add(new Point(rect.X + first.X * rect.Width, rect.Y + first.Y * rect.Height));
            MaskLayer.Children.Add(_activePolyline);
        }
        _activePolyline.Points.Add(new Point(clampedX, clampedY));
    }

    private void Stage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDrawing) return;
        _isDrawing = false;
        _activeStroke = null;
        _activePolyline = null;
        _activeDot = null;
        if (StageBorder.IsMouseCaptured) StageBorder.ReleaseMouseCapture();
    }

    private void Stage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isDrawing = false;
        _activeStroke = null;
        _activePolyline = null;
        _activeDot = null;
    }

    private void Stage_MouseLeave(object sender, MouseEventArgs e)
    {
        if (BrushCursor != null) BrushCursor.Visibility = Visibility.Collapsed;
    }

    private void UpdateCursorVisual()
    {
        if (BrushCursor == null) return;
        BrushCursor.Width = BrushSize;
        BrushCursor.Height = BrushSize;
        BrushCursor.Stroke = IsEraseMode ? FrozenCursorEraseStroke : FrozenCursorPaintStroke;
        BrushCursor.Fill = IsEraseMode ? FrozenCursorEraseFill : FrozenCursorPaintFill;
    }

    private void UpdateCursorPosition(Point pos)
    {
        if (BrushCursor == null) return;
        if (BrushCursor.Visibility != Visibility.Visible)
        {
            UpdateCursorVisual();
            BrushCursor.Visibility = Visibility.Visible;
        }
        Canvas.SetLeft(BrushCursor, pos.X - BrushSize * 0.5);
        Canvas.SetTop(BrushCursor, pos.Y - BrushSize * 0.5);
    }

    private void RedrawMaskLayer()
    {
        if (MaskLayer == null) return;
        MaskLayer.Children.Clear();
        var rect = GetImageRectInStage();
        if (rect.IsEmpty || _strokes.Count == 0) return;

        foreach (var stroke in _strokes)
        {
            var diameter = Math.Max(4.0, stroke.RelativeBrushRadius * 2.0 * rect.Width);
            var brush = stroke.IsErase ? FrozenEraseBrush : FrozenPaintBrush;

            if (stroke.NormalizedPoints.Count == 1)
            {
                var p = stroke.NormalizedPoints[0];
                var dot = new Ellipse { Width = diameter, Height = diameter, Fill = brush };
                Canvas.SetLeft(dot, rect.X + p.X * rect.Width - diameter * 0.5);
                Canvas.SetTop(dot, rect.Y + p.Y * rect.Height - diameter * 0.5);
                MaskLayer.Children.Add(dot);
            }
            else
            {
                var poly = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = diameter,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
                foreach (var p in stroke.NormalizedPoints)
                    poly.Points.Add(new Point(rect.X + p.X * rect.Width, rect.Y + p.Y * rect.Height));
                MaskLayer.Children.Add(poly);
            }
        }
    }

    /// <summary>
    /// Renders painted strokes as a clean white-on-black PNG mask matching the exact dimensions of Source.
    /// </summary>
    public byte[]? ExportMaskPngBytes()
    {
        if (Source is not BitmapSource bmp || _strokes.Count == 0 || !_strokes.Any(s => !s.IsErase))
            return null;

        int w = bmp.PixelWidth;
        int h = bmp.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            foreach (var stroke in _strokes)
            {
                var diameter = Math.Max(4.0, stroke.RelativeBrushRadius * 2.0 * w);
                var brush = stroke.IsErase ? Brushes.Black : Brushes.White;
                if (stroke.NormalizedPoints.Count == 1)
                {
                    var p = stroke.NormalizedPoints[0];
                    dc.DrawEllipse(brush, null, new Point(p.X * w, p.Y * h), diameter * 0.5, diameter * 0.5);
                }
                else
                {
                    var pen = new Pen(brush, diameter)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round
                    };
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        var first = stroke.NormalizedPoints[0];
                        ctx.BeginFigure(new Point(first.X * w, first.Y * h), false, false);
                        for (int i = 1; i < stroke.NormalizedPoints.Count; i++)
                        {
                            var pt = stroke.NormalizedPoints[i];
                            ctx.LineTo(new Point(pt.X * w, pt.Y * h), true, true);
                        }
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                }
            }
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoLastStroke();
    private void Clear_Click(object sender, RoutedEventArgs e) => ClearMask();
}
