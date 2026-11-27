using System.Windows;
using System.Windows.Media;

namespace CharacterConceptAI.Desktop.Components;

/// <summary>
/// Attached properties supplying artwork, crop alignment, and hover-reveal metadata
/// for Gula Studio's asymmetric Bento showcase tiles.
/// </summary>
public static class BentoShowcaseCard
{
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.RegisterAttached(
            "ImageSource",
            typeof(ImageSource),
            typeof(BentoShowcaseCard),
            new PropertyMetadata(null));

    public static void SetImageSource(DependencyObject element, ImageSource? value) =>
        element.SetValue(ImageSourceProperty, value);

    public static ImageSource? GetImageSource(DependencyObject element) =>
        (ImageSource?)element.GetValue(ImageSourceProperty);

    public static readonly DependencyProperty ImageAlignmentYProperty =
        DependencyProperty.RegisterAttached(
            "ImageAlignmentY",
            typeof(AlignmentY),
            typeof(BentoShowcaseCard),
            new PropertyMetadata(AlignmentY.Center));

    public static void SetImageAlignmentY(DependencyObject element, AlignmentY value) =>
        element.SetValue(ImageAlignmentYProperty, value);

    public static AlignmentY GetImageAlignmentY(DependencyObject element) =>
        (AlignmentY)element.GetValue(ImageAlignmentYProperty);

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.RegisterAttached(
            "Title",
            typeof(string),
            typeof(BentoShowcaseCard),
            new PropertyMetadata(string.Empty));

    public static void SetTitle(DependencyObject element, string value) =>
        element.SetValue(TitleProperty, value);

    public static string GetTitle(DependencyObject element) =>
        (string)element.GetValue(TitleProperty);

    public static readonly DependencyProperty StyleTagProperty =
        DependencyProperty.RegisterAttached(
            "StyleTag",
            typeof(string),
            typeof(BentoShowcaseCard),
            new PropertyMetadata(string.Empty));

    public static void SetStyleTag(DependencyObject element, string value) =>
        element.SetValue(StyleTagProperty, value);

    public static string GetStyleTag(DependencyObject element) =>
        (string)element.GetValue(StyleTagProperty);

    public static readonly DependencyProperty RatioTagProperty =
        DependencyProperty.RegisterAttached(
            "RatioTag",
            typeof(string),
            typeof(BentoShowcaseCard),
            new PropertyMetadata(string.Empty));

    public static void SetRatioTag(DependencyObject element, string value) =>
        element.SetValue(RatioTagProperty, value);

    public static string GetRatioTag(DependencyObject element) =>
        (string)element.GetValue(RatioTagProperty);
}
