using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HrpayrollDesktop;

/// <summary>What the fingerprint on screen is showing.</summary>
public enum ScannerState
{
    /// <summary>Nothing happening: the print glows softly and a slow light passes over it.</summary>
    Idle,
    /// <summary>Someone pressed "Scan fingerprint": rings ripple out to invite a finger.</summary>
    Ready,
    /// <summary>A finger is on the reader and is being matched.</summary>
    Scanning,
    /// <summary>The scan was recorded. Returns to Idle by itself.</summary>
    Success,
    /// <summary>The finger was not recognised. Returns to Idle by itself.</summary>
    Failure,
}

/// <summary>
/// The glowing fingerprint drawn after the reference image: dashed neon ridges, cyan into blue, with a scan
/// line that sweeps over it. Used large on the home screen and small in the scanning pop-up.
/// </summary>
public partial class FingerprintScanner : UserControl
{
    private static readonly Color Cyan = Color.FromRgb(0x06, 0xB6, 0xD4);
    private static readonly Color Blue = Color.FromRgb(0x25, 0x63, 0xEB);
    private static readonly Color Mint = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Color Green = Color.FromRgb(0x05, 0x96, 0x69);
    private static readonly Color Rose = Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly Color Red = Color.FromRgb(0xDC, 0x26, 0x26);

    private static readonly Lazy<Geometry> RidgeGeometry = new(BuildRidges);

    private readonly GradientStop _ridgeStart = new(Cyan, 0);
    private readonly GradientStop _ridgeEnd = new(Blue, 1);
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromSeconds(2.4) };

    public static readonly DependencyProperty ScanProgressProperty = DependencyProperty.Register(
        nameof(ScanProgress), typeof(double), typeof(FingerprintScanner),
        new PropertyMetadata(-0.3, (d, e) => ((FingerprintScanner)d).PlaceScanLine((double)e.NewValue)));

    /// <summary>Where the scan line is, from 0 (top of the print) to 1 (bottom). Animated.</summary>
    public double ScanProgress
    {
        get => (double)GetValue(ScanProgressProperty);
        set => SetValue(ScanProgressProperty, value);
    }

    public ScannerState State { get; private set; } = ScannerState.Idle;

    public FingerprintScanner()
    {
        InitializeComponent();

        Ridges.Data = RidgeGeometry.Value;
        RidgesLit.Data = RidgeGeometry.Value;
        Ridges.Stroke = new LinearGradientBrush(new GradientStopCollection { _ridgeStart, _ridgeEnd }, new Point(0.15, 0), new Point(0.85, 1));

        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            SetState(ScannerState.Idle);
        };

        // Animations stop when the control leaves the screen and resume when it comes back.
        Loaded += (_, _) => Apply(State);
        Unloaded += (_, _) => StopAll();
    }

    public void SetState(ScannerState state)
    {
        State = state;
        _settle.Stop();
        if (IsLoaded) Apply(state);
        if (state is ScannerState.Success or ScannerState.Failure) _settle.Start();
    }

    private void Apply(ScannerState state)
    {
        StopAll();

        switch (state)
        {
            case ScannerState.Idle:
                Tint(Cyan, Blue);
                Breathe(3.2);
                Sweep(slow: true, lineOpacity: 0.5);
                RidgesLit.Opacity = 0.6;
                RidgeGlow.BlurRadius = 14;
                break;

            case ScannerState.Ready:
                Tint(Cyan, Blue);
                Breathe(1.6);
                Sweep(slow: true, lineOpacity: 0.85);
                RidgesLit.Opacity = 0.8;
                RidgeGlow.BlurRadius = 18;
                Ripple(RippleA, RippleScaleA, 2.2, TimeSpan.Zero);
                Ripple(RippleB, RippleScaleB, 2.2, TimeSpan.FromSeconds(1.1));
                break;

            case ScannerState.Scanning:
                Tint(Cyan, Blue);
                Breathe(0.7);
                Sweep(slow: false, lineOpacity: 1);
                RidgesLit.Opacity = 1;
                RidgeGlow.BlurRadius = 24;
                Ripple(RippleA, RippleScaleA, 1.1, TimeSpan.Zero);
                Ripple(RippleB, RippleScaleB, 1.1, TimeSpan.FromSeconds(0.55));
                break;

            case ScannerState.Success:
                Tint(Mint, Green);
                Halo.Opacity = 1;
                RidgesLit.Opacity = 0;
                RidgeGlow.BlurRadius = 22;
                ShowBadge("", Green);
                Ripple(RippleA, RippleScaleA, 1.2, TimeSpan.Zero, once: true);
                break;

            case ScannerState.Failure:
                Tint(Rose, Red);
                Halo.Opacity = 1;
                RidgesLit.Opacity = 0;
                RidgeGlow.BlurRadius = 22;
                ShowBadge("", Red);
                Shake.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromMilliseconds(420),
                    KeyFrames =
                    {
                        new LinearDoubleKeyFrame(-9, KeyTime.FromPercent(0.15)),
                        new LinearDoubleKeyFrame(8, KeyTime.FromPercent(0.35)),
                        new LinearDoubleKeyFrame(-6, KeyTime.FromPercent(0.55)),
                        new LinearDoubleKeyFrame(4, KeyTime.FromPercent(0.75)),
                        new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)),
                    },
                });
                break;
        }
    }

    private void StopAll()
    {
        BeginAnimation(ScanProgressProperty, null);
        ScanProgress = -0.3;
        Halo.BeginAnimation(OpacityProperty, null);
        Shake.BeginAnimation(TranslateTransform.XProperty, null);
        foreach (var (ring, scale) in new[] { (RippleA, RippleScaleA), (RippleB, RippleScaleB) })
        {
            ring.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.Opacity = 0;
        }
        Badge.BeginAnimation(OpacityProperty, null);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Badge.Opacity = 0;
        ScanLine.Opacity = 0;
    }

    // ----- Pieces of each state -----

    private void Tint(Color start, Color end)
    {
        var time = TimeSpan.FromMilliseconds(260);
        _ridgeStart.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(start, time));
        _ridgeEnd.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(end, time));
        RidgeGlow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(start, time));
        RippleBrushA.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(start, time));
        RippleBrushB.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(start, time));
        HaloInner.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x60, start.R, start.G, start.B), time));
        HaloMid.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0x1A, end.R, end.G, end.B), time));
    }

    private void Breathe(double seconds) =>
        Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

    private void Sweep(bool slow, double lineOpacity)
    {
        ScanLine.Opacity = lineOpacity;

        AnimationTimeline sweep = slow
            // One gentle pass, then a pause, so an idle screen still looks alive without being busy.
            ? new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(4.6),
                RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(-0.3, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.6)), new SineEase { EasingMode = EasingMode.EaseInOut }),
                    new LinearDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.6))),
                },
            }
            // Back and forth like a real reader while the finger is being matched.
            : new DoubleAnimation(-0.05, 1.05, TimeSpan.FromSeconds(0.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };

        BeginAnimation(ScanProgressProperty, sweep);
    }

    private static void Ripple(UIElement ring, ScaleTransform scale, double seconds, TimeSpan begin, bool once = false)
    {
        var repeat = once ? new RepeatBehavior(1) : RepeatBehavior.Forever;
        var grow = new DoubleAnimation(0.72, 1.18, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = begin,
            RepeatBehavior = repeat,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.75, 0, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = begin,
            RepeatBehavior = repeat,
        });
    }

    private void ShowBadge(string icon, Color color)
    {
        BadgeIcon.Text = icon;
        BadgeDot.Fill = new SolidColorBrush(color);
        BadgeGlow.Color = color;

        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        Badge.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // The bright copy shows only in a band just above the line, so the ridges seem to light up as it passes.
    private void PlaceScanLine(double progress)
    {
        MaskA.Offset = progress - 0.24;
        MaskB.Offset = progress;
        MaskC.Offset = progress + 0.015;
        ScanLineMove.Y = progress * 220 - 1.5;
    }

    // ===================================================================
    //  The ridges
    // ===================================================================

    private const double Size = 220;
    private const double ClipRadius = 104;
    private const double CoreX = 122;   // the loop sits a little right of centre, as its ridges sweep left
    private const double CoreY = 94;

    /// <summary>
    /// Nested loops around a core, whose lower ends sweep down and to the left, cut to a circle and broken
    /// into dashes: the look of the reference print. A fixed seed keeps the pattern the same on every start.
    /// </summary>
    private static Geometry BuildRidges()
    {
        var random = new Random(20261106);
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            for (var ring = 0; ring < 11; ring++)
            {
                var radius = 8 + ring * 9.7;
                foreach (var run in InsideCircle(LoopPoints(radius)))
                {
                    foreach (var dash in Dashes(run, random, longDashes: ring < 2))
                    {
                        context.BeginFigure(dash[0], false, false);
                        context.PolyLineTo(dash.GetRange(1, dash.Count - 1), true, true);
                    }
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // One ridge: up the left leg, over the top, down the right leg.
    private static List<Point> LoopPoints(double radius)
    {
        const double leg = 240;
        const double step = 2;
        var points = new List<Point>();

        for (var y = leg; y > 0; y -= step) points.Add(Bend(-radius, y));
        for (var degrees = 180.0; degrees >= 0; degrees -= 1.5)
        {
            var angle = degrees * Math.PI / 180;
            points.Add(Bend(radius * Math.Cos(angle), -radius * Math.Sin(angle) * 1.08));
        }
        for (var y = step; y <= leg; y += step) points.Add(Bend(radius, y));

        return points;
    }

    // Below the core the ridges lean further left the lower they go, and both legs lean by the same amount,
    // so neighbouring ridges never cross.
    private static Point Bend(double x, double y)
    {
        var lean = y <= 0 ? 0 : 0.36 * y + 0.0014 * y * y;
        return new Point(CoreX + x - lean, CoreY + y);
    }

    // The parts of a ridge that fall inside the round print.
    private static IEnumerable<List<Point>> InsideCircle(List<Point> points)
    {
        var centre = new Point(Size / 2, Size / 2);
        List<Point>? run = null;

        foreach (var point in points)
        {
            if ((point - centre).Length <= ClipRadius)
            {
                run ??= new List<Point>();
                run.Add(point);
            }
            else if (run is not null)
            {
                if (run.Count > 2) yield return run;
                run = null;
            }
        }

        if (run is { Count: > 2 }) yield return run;
    }

    // Breaks a run into dashes of varied length with short gaps, as in the reference.
    private static IEnumerable<List<Point>> Dashes(List<Point> run, Random random, bool longDashes)
    {
        double NextDash() => longDashes ? 70 + random.NextDouble() * 90 : 20 + random.NextDouble() * 78;
        double NextGap() => 8 + random.NextDouble() * 6;

        var drawing = random.NextDouble() > 0.25;
        var left = drawing ? NextDash() * random.NextDouble() + 8 : NextGap();
        var current = new List<Point> { run[0] };
        var length = 0.0;

        for (var i = 1; i < run.Count; i++)
        {
            var from = run[i - 1];
            var to = run[i];
            var segment = (to - from).Length;
            var used = 0.0;

            while (segment - used > left)
            {
                used += left;
                var cut = from + (to - from) * (used / segment);

                if (drawing)
                {
                    current.Add(cut);
                    length += left;
                    if (length >= 5 && current.Count > 1) yield return current;
                }

                drawing = !drawing;
                current = new List<Point> { cut };
                length = 0;
                left = drawing ? NextDash() : NextGap();
            }

            left -= segment - used;
            if (drawing)
            {
                current.Add(to);
                length += segment - used;
            }
        }

        if (drawing && length >= 5 && current.Count > 1) yield return current;
    }
}
