using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CharacterConceptAI.Desktop.Components;

public partial class ImageCompareSlider : UserControl
{
    public static readonly DependencyProperty BeforeSourceProperty =
        DependencyProperty.Register(nameof(BeforeSource), typeof(ImageSource), typeof(ImageCompareSlider), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty AfterSourceProperty =
        DependencyProperty.Register(nameof(AfterSource), typeof(ImageSource), typeof(ImageCompareSlider), new PropertyMetadata(null, OnSourceChanged));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImageCompareSlider slider)
            slider.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, slider.UpdateSplitVisual);
    }

    public static readonly DependencyProperty BeforeLabelProperty =
        DependencyProperty.Register(nameof(BeforeLabel), typeof(string), typeof(ImageCompareSlider), new PropertyMetadata("Ảnh gốc (1×)"));

    public static readonly DependencyProperty AfterLabelProperty =
        DependencyProperty.Register(nameof(AfterLabel), typeof(string), typeof(ImageCompareSlider), new PropertyMetadata("Đã nâng cấp (Full 2×)"));

    public ImageSource? BeforeSource
    {
        get => (ImageSource?)GetValue(BeforeSourceProperty);
        set => SetValue(BeforeSourceProperty, value);
    }

    public ImageSource? AfterSource
    {
        get => (ImageSource?)GetValue(AfterSourceProperty);
        set => SetValue(AfterSourceProperty, value);
    }

    public string BeforeLabel
    {
        get => (string)GetValue(BeforeLabelProperty);
        set => SetValue(BeforeLabelProperty, value);
    }

    public string AfterLabel
    {
        get => (string)GetValue(AfterLabelProperty);
        set => SetValue(AfterLabelProperty, value);
    }

    private bool _dragging;
    private double _ratio = 0.5;

    public ImageCompareSlider()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateSplitVisual();
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSplitVisual();

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _ratio = Math.Clamp(e.NewValue, 0.02, 0.98);
        UpdateSplitVisual();
    }

    private void ResetSplit_Click(object sender, RoutedEventArgs e)
    {
        PositionSlider.Value = 0.5;
    }

    private void Stage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        Stage.CaptureMouse();
        UpdateRatioFromPoint(e.GetPosition(Stage));
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateRatioFromPoint(e.GetPosition(Stage));
    }

    private void Stage_MouseUp(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (Stage.IsMouseCaptured)
            Stage.ReleaseMouseCapture();
    }

    private void Stage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
    }

    private void UpdateRatioFromPoint(Point p)
    {
        if (Stage.ActualWidth <= 1) return;
        var next = Math.Clamp(p.X / Stage.ActualWidth, 0.02, 0.98);
        PositionSlider.Value = next;
    }

    private void UpdateSplitVisual()
    {
        if (Stage == null || BeforeClipRect == null || DividerLine == null || DividerHandle == null) return;
        var w = Stage.ActualWidth;
        var h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var splitX = w * _ratio;
        BeforeClipRect.Rect = new Rect(0, 0, splitX, h);
        DividerLine.Height = h;
        Canvas.SetLeft(DividerLine, splitX - 1);
        Canvas.SetLeft(DividerHandle, splitX - (DividerHandle.Width / 2));
        Canvas.SetTop(DividerHandle, (h - DividerHandle.Height) / 2);
    }
}
