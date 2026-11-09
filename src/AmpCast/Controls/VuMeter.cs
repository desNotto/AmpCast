using System;
using System.Windows;
using System.Windows.Media;

namespace AmpCast.Controls;

/// <summary>
/// Segmented vertical peak meter, -60…+3 dBFS, blurple → purple → red,
/// with a peak-hold segment. Brushes are frozen and shared; OnRender
/// allocates nothing but the Rect structs.
/// </summary>
public sealed class VuMeter : FrameworkElement
{
    private const int Segments = 32;
    private const double MinDb = -60, MaxDb = 3, Gap = 2;

    private static readonly Brush[] OnBrushes = new Brush[Segments];
    private static readonly Brush OffBrush;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VuMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(VuMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static VuMeter()
    {
        Color blurple = Color.FromRgb(0x58, 0x65, 0xF2);
        Color purple = Color.FromRgb(0xAF, 0x47, 0xFF);
        Color pink = Color.FromRgb(0xE8, 0x4F, 0xC8);
        Color red = Color.FromRgb(0xF2, 0x3F, 0x43);
        double zeroDbFraction = (0 - MinDb) / (MaxDb - MinDb);

        for (int i = 0; i < Segments; i++)
        {
            double t = (i + 0.5) / Segments;
            Color c = t < 0.72 ? Lerp(blurple, purple, t / 0.72)
                    : t < zeroDbFraction ? Lerp(purple, pink, (t - 0.72) / (zeroDbFraction - 0.72))
                    : red;
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            OnBrushes[i] = brush;
        }
        OffBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x30, 0x36));
        OffBrush.Freeze();
    }

    /// <summary>Current level, linear amplitude (1.0 = 0 dBFS).</summary>
    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Peak-hold level, linear amplitude.</summary>
    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public static double ToFraction(double linear)
    {
        if (linear <= 1e-6) return 0;
        double db = 20 * Math.Log10(linear);
        return Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double segH = (h - Gap * (Segments - 1)) / Segments;
        if (segH <= 0.5) return;
        double radius = Math.Min(2, Math.Min(w, segH) / 2);

        int lit = (int)Math.Round(ToFraction(Level) * Segments);
        int peak = (int)Math.Round(ToFraction(Peak) * Segments) - 1;

        for (int i = 0; i < Segments; i++)
        {
            double y = h - (i + 1) * segH - i * Gap;
            Brush b = i < lit || i == peak ? OnBrushes[i] : OffBrush;
            dc.DrawRoundedRectangle(b, null, new Rect(0, y, w, segH), radius, radius);
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}

/// <summary>Meter ballistics: instant attack, ~26 dB/s release, 1.2 s peak hold.</summary>
public sealed class MeterBallistics
{
    private double _level, _peak, _holdSeconds;

    public double Level => _level;
    public double Peak => _peak;

    public void Update(double input, double dt)
    {
        double release = Math.Pow(10, -26 * dt / 20);
        _level = Math.Max(input, _level * release);
        if (input >= _peak)
        {
            _peak = input;
            _holdSeconds = 1.2;
        }
        else if ((_holdSeconds -= dt) <= 0)
        {
            _peak *= Math.Pow(10, -40 * dt / 20);
        }
    }
}
