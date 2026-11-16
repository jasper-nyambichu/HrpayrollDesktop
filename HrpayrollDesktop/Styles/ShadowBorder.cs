using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HrpayrollDesktop;

public enum Elevation { None, Soft, Mid, Large }

/// <summary>
/// A Border that paints its own soft shadow underneath itself.
///
/// A DropShadowEffect on a Border renders the Border and everything inside it through an off-screen bitmap: the
/// text in it loses ClearType and turns soft. That is barely visible on a sharp laptop screen but plainly blurry on
/// an ordinary 100%-scale monitor, or on a PC without a proper graphics driver. Here the shadow is drawn as a few
/// faint rounded rectangles below the panel, so what the panel holds is drawn directly and stays sharp.
/// </summary>
public class ShadowBorder : Border
{
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(
        nameof(Elevation), typeof(Elevation), typeof(ShadowBorder),
        new FrameworkPropertyMetadata(Elevation.Soft, FrameworkPropertyMetadataOptions.AffectsRender));

    public Elevation Elevation
    {
        get => (Elevation)GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    // The same blur, offset and strength as the ShadowSoft / ShadowMid / ShadowLarge effects they replace.
    private static (double Blur, double Depth, double Opacity) Look(Elevation e) => e switch
    {
        Elevation.Soft => (14, 2, 0.08),
        Elevation.Mid => (28, 6, 0.12),
        Elevation.Large => (60, 16, 0.22),
        _ => (0, 0, 0),
    };

    private const int Steps = 10;
    private static readonly Color Ink = Color.FromRgb(0x0B, 0x2A, 0x45);

    protected override void OnRender(DrawingContext dc)
    {
        var (blur, depth, opacity) = Look(Elevation);
        if (blur > 0 && ActualWidth > 0 && ActualHeight > 0)
        {
            // Each ring adds a little; stacked they fade from `opacity` at the panel's edge to nothing half the
            // blur radius further out, which is how a blurred shadow looks.
            var ring = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity / 2 / Steps), Ink.R, Ink.G, Ink.B));
            ring.Freeze();
            var core = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity / 2), Ink.R, Ink.G, Ink.B));
            core.Freeze();

            var radius = CornerRadius.TopLeft;
            var box = new Rect(0, depth, ActualWidth, ActualHeight);
            var reach = blur / 2;
            for (var i = Steps; i >= 1; i--)
            {
                var grow = reach * i / Steps;
                var r = box;
                r.Inflate(grow, grow);
                dc.DrawRoundedRectangle(ring, null, r, radius + grow, radius + grow);
            }
            dc.DrawRoundedRectangle(core, null, box, radius, radius);
        }
        base.OnRender(dc);
    }

    // The shadow is decoration: a click on it belongs to whatever is under it, as with the effect before.
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? base.HitTestCore(hitTestParameters) : null;
}
