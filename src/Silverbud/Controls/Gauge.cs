using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Silverbud.Controls;

/// <summary>Shared look for the battery "blade light" gauges: LED colours and the charge-up motion.</summary>
internal static class Gauge
{
    public static readonly Color Green = Color.FromRgb(0x2E, 0x9E, 0x68);
    public static readonly Color Amber = Color.FromRgb(0xD4, 0x88, 0x1E);
    public static readonly Color Red = Color.FromRgb(0xCF, 0x44, 0x36);

    public static Color LevelColor(int level) => level < 20 ? Red : level < 50 ? Amber : Green;

    public static void Fill(ScaleTransform transform, DependencyProperty axis, int level)
    {
        double target = level < 0 ? 0 : Math.Max(level, 3) / 100.0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(axis, null);
            transform.SetValue(axis, target);
            return;
        }
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(800))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(axis, animation);
    }

    public static void Pulse(UIElement element, bool on)
    {
        if (on && SystemParameters.ClientAreaAnimation)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(1.1))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        else
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        }
    }
}
