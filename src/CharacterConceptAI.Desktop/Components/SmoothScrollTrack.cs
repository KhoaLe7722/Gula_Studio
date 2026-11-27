using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CharacterConceptAI.Desktop.Components;

/// <summary>
/// Custom WPF Track that guarantees a grabbable minimum Thumb length on very long
/// ScrollViewers (e.g. 50-turn image chats) WITHOUT letting WPF clip the Thumb visual
/// or introducing drag-offset drift.
/// </summary>
public sealed class SmoothScrollTrack : Track
{
    public static readonly DependencyProperty MinThumbLengthProperty = DependencyProperty.Register(
        nameof(MinThumbLength),
        typeof(double),
        typeof(SmoothScrollTrack),
        new FrameworkPropertyMetadata(44.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinThumbLength
    {
        get => (double)GetValue(MinThumbLengthProperty);
        set => SetValue(MinThumbLengthProperty, value);
    }

    private double _lastTravel = 1.0;
    private double _lastRange;
    private double _lastThumbLength = 44.0;

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var range = Maximum - Minimum;
        var isVertical = Orientation == Orientation.Vertical;
        var trackLength = isVertical ? arrangeSize.Height : arrangeSize.Width;

        if (Thumb == null || !double.IsFinite(range) || range <= 0 || !double.IsFinite(ViewportSize) || ViewportSize <= 0 || !double.IsFinite(trackLength) || trackLength <= 0)
        {
            _lastRange = 0;
            _lastTravel = 0;
            if (Visibility != Visibility.Hidden)
                Visibility = Visibility.Hidden;
            return arrangeSize;
        }

        // Critical: WPF's Track hides itself (this.Visibility = Visibility.Hidden) when range <= 0.
        // Explicitly restore Track & child visibility whenever scrollable content is present.
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
        if (Thumb.Visibility != Visibility.Visible)
            Thumb.Visibility = Visibility.Visible;
        if (DecreaseRepeatButton != null && DecreaseRepeatButton.Visibility != Visibility.Visible)
            DecreaseRepeatButton.Visibility = Visibility.Visible;
        if (IncreaseRepeatButton != null && IncreaseRepeatButton.Visibility != Visibility.Visible)
            IncreaseRepeatButton.Visibility = Visibility.Visible;

        var proportional = trackLength * (ViewportSize / (range + ViewportSize));
        var minThumb = Math.Min(MinThumbLength, Math.Max(20.0, trackLength * 0.45));
        var thumbLength = Math.Clamp(proportional, minThumb, trackLength);
        var travel = Math.Max(0.0, trackLength - thumbLength);

        _lastRange = range;
        _lastTravel = travel;
        _lastThumbLength = thumbLength;

        var ratio = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);
        var offset = isVertical
            ? (IsDirectionReversed ? ratio * travel : (1.0 - ratio) * travel)
            : (IsDirectionReversed ? (1.0 - ratio) * travel : ratio * travel);

        var afterOffset = offset + thumbLength;
        var remaining = Math.Max(0.0, trackLength - afterOffset);

        if (isVertical)
        {
            var w = arrangeSize.Width;
            if (IsDirectionReversed)
            {
                DecreaseRepeatButton?.Arrange(new Rect(0, 0, w, offset));
                Thumb.Arrange(new Rect(0, offset, w, thumbLength));
                IncreaseRepeatButton?.Arrange(new Rect(0, afterOffset, w, remaining));
            }
            else
            {
                IncreaseRepeatButton?.Arrange(new Rect(0, 0, w, offset));
                Thumb.Arrange(new Rect(0, offset, w, thumbLength));
                DecreaseRepeatButton?.Arrange(new Rect(0, afterOffset, w, remaining));
            }
        }
        else
        {
            var h = arrangeSize.Height;
            if (!IsDirectionReversed)
            {
                DecreaseRepeatButton?.Arrange(new Rect(0, 0, offset, h));
                Thumb.Arrange(new Rect(offset, 0, thumbLength, h));
                IncreaseRepeatButton?.Arrange(new Rect(afterOffset, 0, remaining, h));
            }
            else
            {
                IncreaseRepeatButton?.Arrange(new Rect(0, 0, offset, h));
                Thumb.Arrange(new Rect(offset, 0, thumbLength, h));
                DecreaseRepeatButton?.Arrange(new Rect(afterOffset, 0, remaining, h));
            }
        }

        return arrangeSize;
    }

    public override double ValueFromDistance(double horizontal, double vertical)
    {
        if (_lastRange <= 0 || _lastTravel <= 0)
            return 0;

        var deltaPerPixel = _lastRange / _lastTravel;
        if (Orientation == Orientation.Vertical)
        {
            // Standard vertical ScrollBar uses IsDirectionReversed=True so dragging DOWN (+vertical) increases Value.
            return IsDirectionReversed ? vertical * deltaPerPixel : -vertical * deltaPerPixel;
        }
        return IsDirectionReversed ? -horizontal * deltaPerPixel : horizontal * deltaPerPixel;
    }

    public override double ValueFromPoint(Point pt)
    {
        if (_lastRange <= 0 || _lastTravel <= 0)
            return Minimum;

        var pos = Orientation == Orientation.Vertical ? pt.Y : pt.X;
        var centered = Math.Clamp(pos - (_lastThumbLength * 0.5), 0.0, _lastTravel);
        var ratio = centered / _lastTravel;
        var directed = Orientation == Orientation.Vertical
            ? (IsDirectionReversed ? ratio : 1.0 - ratio)
            : (IsDirectionReversed ? 1.0 - ratio : ratio);
        return Minimum + (directed * _lastRange);
    }
}
