using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CharacterConceptAI.Desktop.Components;

public partial class ImageViewport : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ImageViewport),
        new PropertyMetadata(null, OnSourceChanged));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public static readonly DependencyProperty ShowTransparencyGridProperty = DependencyProperty.Register(
        nameof(ShowTransparencyGrid), typeof(bool), typeof(ImageViewport),
        new PropertyMetadata(false));
    public bool ShowTransparencyGrid { get => (bool)GetValue(ShowTransparencyGridProperty); set => SetValue(ShowTransparencyGridProperty, value); }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImageViewport vp && vp.Zoom != null && vp.Viewport != null)
        {
            vp.Zoom.Value = 1;
            vp.Viewport.ScrollToHome();
        }
    }

    private bool _isPanning;
    private Point _panStart;
    private double _startHOffset;
    private double _startVOffset;

    public ImageViewport() => InitializeComponent();

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        Zoom.Value = 1;
        Viewport.ScrollToHome();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => Zoom.Value = Math.Min(Zoom.Maximum, Math.Round((Zoom.Value + 0.25) * 4) / 4.0);

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => Zoom.Value = Math.Max(Zoom.Minimum, Math.Round((Zoom.Value - 0.25) * 4) / 4.0);

    private void Viewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Source == null) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 || Zoom.Value > 1.01)
        {
            var step = e.Delta > 0 ? 0.2 : -0.2;
            Zoom.Value = Math.Clamp(Zoom.Value + step, Zoom.Minimum, Zoom.Maximum);
            e.Handled = true;
        }
    }

    private static bool IsInsideScrollBar(DependencyObject? source, DependencyObject stopAt)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, stopAt))
        {
            if (current is System.Windows.Controls.Primitives.ScrollBar)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Source == null) return;
        if (IsInsideScrollBar(e.OriginalSource as DependencyObject, Viewport)) return;
        if (e.ClickCount == 2)
        {
            Zoom.Value = Zoom.Value > 1.05 ? 1.0 : 2.0;
            if (Zoom.Value <= 1.05) Viewport.ScrollToHome();
            e.Handled = true;
            return;
        }
        if (Zoom.Value <= 1.01) return;
        _isPanning = true;
        _panStart = e.GetPosition(Viewport);
        _startHOffset = Viewport.HorizontalOffset;
        _startVOffset = Viewport.VerticalOffset;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var current = e.GetPosition(Viewport);
        Viewport.ScrollToHorizontalOffset(_startHOffset - (current.X - _panStart.X));
        Viewport.ScrollToVerticalOffset(_startVOffset - (current.Y - _panStart.Y));
    }

    private void Viewport_MouseUp(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        if (Viewport.IsMouseCaptured)
            Viewport.ReleaseMouseCapture();
        Viewport.Cursor = Cursors.Hand;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        Viewport.Cursor = Cursors.Hand;
    }
}
