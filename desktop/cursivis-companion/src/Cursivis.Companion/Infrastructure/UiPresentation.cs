using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Cursivis.Companion.Infrastructure;

public static class UiPresentation
{
    public static void ApplyShinyText(
        TextBlock target,
        Color baseColor,
        Color shineColor,
        double speedSeconds = 2.2,
        double shineStrength = 0.46,
        double overscan = 1.18)
    {
        shineStrength = Math.Clamp(shineStrength, 0.08, 1.0);
        overscan = Math.Clamp(overscan, 1.02, 1.6);

        var softShine = Blend(baseColor, shineColor, shineStrength * 0.34);
        var midShine = Blend(baseColor, shineColor, shineStrength * 0.62);
        var peakShine = Blend(baseColor, shineColor, shineStrength);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = new TranslateTransform(overscan, 0)
        };

        brush.GradientStops.Add(new GradientStop(baseColor, 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.16));
        brush.GradientStops.Add(new GradientStop(softShine, 0.31));
        brush.GradientStops.Add(new GradientStop(midShine, 0.43));
        brush.GradientStops.Add(new GradientStop(peakShine, 0.5));
        brush.GradientStops.Add(new GradientStop(midShine, 0.57));
        brush.GradientStops.Add(new GradientStop(softShine, 0.69));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.84));
        brush.GradientStops.Add(new GradientStop(baseColor, 1.0));

        target.Foreground = brush;

        if (brush.RelativeTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation
                {
                    From = overscan,
                    To = -overscan,
                    Duration = TimeSpan.FromSeconds(speedSeconds),
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
    }

    private static Color Blend(Color from, Color to, double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * factor));
        return Color.FromArgb(
            Mix(from.A, to.A),
            Mix(from.R, to.R),
            Mix(from.G, to.G),
            Mix(from.B, to.B));
    }

    public static void SetFlatText(TextBlock target, Color color)
    {
        target.Foreground = new SolidColorBrush(color);
    }

    public static void AnimateEntrance(FrameworkElement target, TranslateTransform translateTransform, double fromY = 16, double durationMs = 260)
    {
        target.Opacity = 0;
        translateTransform.Y = fromY;

        target.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        translateTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation
            {
                From = fromY,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    public static async Task RevealTextAsync(TextBlock target, string text, CancellationToken cancellationToken)
    {
        var normalized = text ?? string.Empty;
        target.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var stride = Math.Clamp(normalized.Length / 140, 1, 18);
        var delay = normalized.Length > 1800 ? 5 : normalized.Length > 700 ? 8 : 12;

        for (var index = 0; index < normalized.Length; index += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(normalized.Length, index + stride);
            target.Text = normalized[..length];
            await Task.Delay(delay, cancellationToken);
        }

        target.Text = normalized;
    }
}
