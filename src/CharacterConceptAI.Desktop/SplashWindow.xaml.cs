using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CharacterConceptAI.Desktop;

public partial class SplashWindow : Window
{
    private double _currentPercent = 5.0;
    private bool _posterVisible;

    public SplashWindow()
    {
        InitializeComponent();

        // Scale to 3x+ cinema area (1632x932 outer => exact 1600x900 16:9 card) filling up to 96% of WorkArea cleanly
        var work = SystemParameters.WorkArea;
        var maxW = Math.Max(840.0, work.Width * 0.96);
        var maxH = Math.Max(480.0, work.Height * 0.96);
        var scale = Math.Min(1.0, Math.Min(maxW / 1632.0, maxH / 932.0));
        Width = Math.Round(1632.0 * scale);
        Height = Math.Round(932.0 * scale);
        Left = work.Left + (work.Width - Width) * 0.5;
        Top = work.Top + (work.Height - Height) * 0.5;

        Loaded += (_, _) => StartLogoIntroAnimation();
    }

    private void StartLogoIntroAnimation()
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Dramatic fade-in + scale-in of the giant floating Gula logo
        LogoIntroLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = easeOut });

        var scaleIn = new DoubleAnimationUsingKeyFrames();
        scaleIn.KeyFrames.Add(new EasingDoubleKeyFrame(0.76, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), easeOut));
        scaleIn.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        LogoStageScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        LogoStageScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn.Clone());

        // Breathing Volt-Lime neon aura pulse
        var auraPulse = new DoubleAnimation(0.58, 1.0, TimeSpan.FromMilliseconds(1150))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoGlowAura.BeginAnimation(OpacityProperty, auraPulse);

        // Expanding neon accent line + sliding wordmark
        IntroAccentLine.BeginAnimation(WidthProperty, new DoubleAnimation(0, 340, TimeSpan.FromMilliseconds(620))
        {
            BeginTime = TimeSpan.FromMilliseconds(260),
            EasingFunction = easeOut
        });
        IntroWordmark.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(540))
        {
            BeginTime = TimeSpan.FromMilliseconds(240),
            EasingFunction = easeOut
        });
        IntroWordmarkSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(540))
        {
            BeginTime = TimeSpan.FromMilliseconds(240),
            EasingFunction = easeOut
        });
    }

    public async Task PlayLogoIntroAndRevealPosterAsync(int logoDurationMs = 3000)
    {
        if (logoDurationMs > 0)
            await Task.Delay(logoDurationMs);

        await Dispatcher.InvokeAsync(() =>
        {
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Stage 1 gracefully dissolves outward
            LogoIntroLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(LogoIntroLayer.Opacity, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = easeIn });
            LogoStageScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(LogoStageScale.ScaleX, 1.12, TimeSpan.FromMilliseconds(360)) { EasingFunction = easeIn });
            LogoStageScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(LogoStageScale.ScaleY, 1.12, TimeSpan.FromMilliseconds(360)) { EasingFunction = easeIn });

            // Stage 2 (big 2x artistic Gula Studio poster) glides smoothly into view
            PosterLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(440))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = easeOut
            });
            PosterScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(440))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = easeOut
            });
            PosterScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(440))
            {
                BeginTime = TimeSpan.FromMilliseconds(80),
                EasingFunction = easeOut
            });
        });

        await Task.Delay(480);

        await Dispatcher.InvokeAsync(() =>
        {
            LogoGlowAura.BeginAnimation(OpacityProperty, null);
            LogoIntroLayer.Visibility = Visibility.Collapsed;
            _posterVisible = true;
            UpdateProgressBarWidth(_currentPercent, animateMs: 220);
        });
    }

    public void ReportProgress(double percent, string message)
    {
        var clamped = Math.Clamp(percent, _currentPercent, 100.0);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportProgress(clamped, message));
            return;
        }

        _currentPercent = clamped;
        StatusLabel.Text = message;
        PercentLabel.Text = $"{Math.Round(clamped):0}%";
        UpdateProgressBarWidth(clamped, animateMs: _posterVisible ? 260 : 80);
    }

    private void UpdateProgressBarWidth(double percent, int animateMs)
    {
        var trackWidth = ProgressTrack.ActualWidth > 40 ? ProgressTrack.ActualWidth : Math.Max(200.0, Width - 84.0);
        var targetWidth = Math.Max(24.0, trackWidth * (percent / 100.0));
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(animateMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressFill.BeginAnimation(WidthProperty, anim);
    }

    public async Task FadeOutAndCloseAsync()
    {
        var tcs = new TaskCompletionSource();
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, fadeOut);
        await tcs.Task;
        Close();
    }
}
