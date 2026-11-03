using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sanova.Launcher.Controls;

/// <summary>
/// The SANOVA mark: a sun setting behind the horizon over three road lines, on a dusk tile.
/// Same geometry as build/generate-icon.ps1 (the application icon).
/// </summary>
public sealed class LogoMark : Control
{
    private static readonly IBrush TileBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0x34, 0x18, 0x40), 0), new GradientStop(Color.FromRgb(0x14, 0x0E, 0x1B), 1) },
    };

    private static readonly IBrush SunBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0xFF, 0xD9, 0x86), 0), new GradientStop(Color.FromRgb(0xFF, 0x7E, 0x4A), 1) },
    };

    private static readonly IBrush RoadBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0x4D));

    public override void Render(DrawingContext context)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s < 4) return;
        var inset = Math.Max(0.5, s * 0.02);
        context.DrawRectangle(TileBrush, null, new RoundedRect(new Rect(inset, inset, s - inset * 2, s - inset * 2), s * 0.22));

        var horizon = s * 0.60;
        var radius = s * 0.25;
        using (context.PushClip(new Rect(0, 0, s, horizon)))
        {
            context.DrawEllipse(SunBrush, null, new Point(s * 0.5, horizon), radius, radius);
        }

        var lineHeight = Math.Max(1.0, s * 0.045);
        double[] widths = [0.56, 0.38, 0.20];
        for (var i = 0; i < widths.Length; i++)
        {
            var width = s * widths[i];
            var y = horizon + s * 0.075 + i * s * 0.095;
            context.DrawRectangle(RoadBrush, null, new RoundedRect(new Rect(s * 0.5 - width / 2, y, width, lineHeight), lineHeight / 2));
        }
    }
}
