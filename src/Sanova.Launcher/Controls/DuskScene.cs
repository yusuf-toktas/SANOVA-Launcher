using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.Controls;

/// <summary>
/// Original, procedurally drawn hero artwork: a smoggy West Coast sunset with mountain ridges,
/// a downtown skyline, a freeway overpass lit by sodium lamps and palm silhouettes.
/// The single animation — lamps warming from deep red to amber, as low-pressure sodium lamps
/// do — plays once per session and is skipped when Windows animations are turned off.
/// </summary>
public sealed class DuskScene : Control
{
    public static readonly StyledProperty<double> WarmupProperty = AvaloniaProperty.Register<DuskScene, double>(nameof(Warmup), 1.0);

    private static bool s_introPlayed;
    private DispatcherTimer? _timer;
    private Size _builtFor;
    private Scene? _scene;

    static DuskScene()
    {
        AffectsRender<DuskScene>(WarmupProperty);
    }

    public DuskScene()
    {
        // Haze bands and the skyline extend past the edges by design; never paint outside our bounds.
        ClipToBounds = true;
    }

    /// <summary>Set to false (e.g. in UI tests) to render the final state immediately.</summary>
    public static bool PlayIntro { get; set; } = true;

    public double Warmup
    {
        get => GetValue(WarmupProperty);
        set => SetValue(WarmupProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (s_introPlayed || !PlayIntro || Design.IsDesignMode || !ShellIntegration.AnimationsEnabled)
        {
            Warmup = 1;
            return;
        }

        s_introPlayed = true;
        Warmup = 0;
        var started = DateTime.UtcNow;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var t = (DateTime.UtcNow - started).TotalSeconds / 2.8;
            if (t >= 1)
            {
                Warmup = 1;
                _timer?.Stop();
                _timer = null;
            }
            else
            {
                Warmup = 1 - Math.Pow(1 - t, 3);
            }
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        Warmup = 1;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;
        if (_scene is null || _builtFor != size)
        {
            _scene = Scene.Build(size.Width, size.Height);
            _builtFor = size;
        }
        _scene.Draw(context, Math.Clamp(Warmup, 0, 1));
    }

    private sealed class Scene
    {
        private static readonly Color Sodium = Color.FromRgb(0xFF, 0xB3, 0x47);
        private static readonly Color ColdLamp = Color.FromRgb(0x9E, 0x2C, 0x1E);

        private double _w;
        private double _h;
        private double _horizon;
        private Rect _skyRect;
        private IBrush _sky = Brushes.Transparent;
        private Point _sunCenter;
        private double _sunRadius;
        private readonly List<(Rect Rect, IBrush Brush)> _haze = [];
        private Rect _horizonGlow;
        private StreamGeometry _farRidge = new();
        private StreamGeometry _nearRidge = new();
        private StreamGeometry _skyline = new();
        private StreamGeometry _windows = new();
        private StreamGeometry _midPalms = new();
        private Rect _ground;
        private StreamGeometry _freeway = new();
        private StreamGeometry _posts = new();
        private readonly List<Point> _lampHeads = [];
        private double _deckTop;
        private StreamGeometry _palms = new();
        private Pen _postPen = new(Brushes.Black);

        public static Scene Build(double w, double h)
        {
            var s = new Scene { _w = w, _h = h, _horizon = h * 0.74 };
            s.BuildSky();
            s.BuildRidges();
            s.BuildSkyline();
            s.BuildFreeway();
            s.BuildPalms();
            return s;
        }

        private void BuildSky()
        {
            _skyRect = new Rect(0, 0, _w, _horizon + 1);
            _sky = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x1A, 0x0F, 0x2E), 0.00),
                    new GradientStop(Color.FromRgb(0x33, 0x16, 0x40), 0.30),
                    new GradientStop(Color.FromRgb(0x6E, 0x25, 0x52), 0.55),
                    new GradientStop(Color.FromRgb(0xC1, 0x4A, 0x4A), 0.74),
                    new GradientStop(Color.FromRgb(0xEE, 0x7E, 0x45), 0.87),
                    new GradientStop(Color.FromRgb(0xFF, 0xB0, 0x5A), 0.96),
                    new GradientStop(Color.FromRgb(0xFF, 0xC8, 0x77), 1.00),
                },
            };

            _sunRadius = _h * 0.17;
            _sunCenter = new Point(_w * 0.67, _horizon - _h * 0.035);

            double[] offsets = [0.18, 0.36, 0.55, 0.76];
            double[] heights = [0.009, 0.012, 0.015, 0.019];
            for (var i = 0; i < offsets.Length; i++)
            {
                var y = _horizon - _sunRadius * offsets[i] - _h * heights[i] / 2;
                var rect = new Rect(_sunCenter.X - _sunRadius * 2.4, y, _sunRadius * 4.8, _h * heights[i]);
                _haze.Add((rect, new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 0xFF, 0x8A, 0x4D), 0),
                        new GradientStop(Color.FromArgb(80, 0xFF, 0x8A, 0x4D), 0.5),
                        new GradientStop(Color.FromArgb(0, 0xFF, 0x8A, 0x4D), 1),
                    },
                }));
            }
            _horizonGlow = new Rect(0, _horizon - _h * 0.11, _w, _h * 0.11);
        }

        private void BuildRidges()
        {
            var y = _horizon;
            _farRidge = Polygon(
            [
                new(0, y), new(0, y - _h * 0.105), new(_w * 0.04, y - _h * 0.128), new(_w * 0.085, y - _h * 0.162),
                new(_w * 0.12, y - _h * 0.198), new(_w * 0.145, y - _h * 0.176), new(_w * 0.175, y - _h * 0.158), new(_w * 0.21, y - _h * 0.166),
                new(_w * 0.25, y - _h * 0.122), new(_w * 0.30, y - _h * 0.094), new(_w * 0.355, y - _h * 0.108), new(_w * 0.41, y - _h * 0.066),
                new(_w * 0.47, y - _h * 0.05), new(_w * 0.54, y - _h * 0.028), new(_w * 0.60, y),
            ]);
            _nearRidge = Polygon(
            [
                new(0, y), new(0, y - _h * 0.052), new(_w * 0.07, y - _h * 0.074), new(_w * 0.15, y - _h * 0.045),
                new(_w * 0.23, y - _h * 0.064), new(_w * 0.30, y - _h * 0.03), new(_w * 0.37, y - _h * 0.038), new(_w * 0.44, y),
            ]);
        }

        private void BuildSkyline()
        {
            var random = new Random(1992);
            var buildings = new StreamGeometry();
            var windows = new StreamGeometry();
            using (var b = buildings.Open())
            using (var win = windows.Open())
            {
                b.SetFillRule(FillRule.NonZero);
                win.SetFillRule(FillRule.NonZero);
                var x = _w * 0.40;
                var tallest = (X: 0.0, Top: double.MaxValue, Width: 0.0);
                while (x < _w * 1.02)
                {
                    var width = _h * (0.03 + random.NextDouble() * 0.05);
                    var downtown = _h * 0.16 * Math.Exp(-Math.Pow((x - _w * 0.73) / (_w * 0.075), 2));
                    var height = _h * (0.022 + random.NextDouble() * 0.055) + downtown * (0.6 + random.NextDouble() * 0.5);
                    var top = _horizon - height;
                    AddRect(b, new Rect(x, top, width, height + 1));
                    if (height > _h * 0.09 && random.NextDouble() < 0.5)
                    {
                        var inset = width * 0.22;
                        var extra = _h * (0.012 + random.NextDouble() * 0.02);
                        AddRect(b, new Rect(x + inset, top - extra, width - inset * 2, extra + 1));
                        top -= extra;
                    }
                    if (top < tallest.Top) tallest = (x, top, width);

                    if (height > _h * 0.07)
                    {
                        var cell = _h * 0.012;
                        var columns = (int)Math.Floor((width - _h * 0.006) / cell);
                        var rows = (int)Math.Floor((height - _h * 0.03) / (_h * 0.016));
                        for (var row = 0; row < rows; row++)
                        {
                            for (var column = 0; column < columns; column++)
                            {
                                if (random.NextDouble() < 0.17)
                                {
                                    AddRect(win, new Rect(x + _h * 0.005 + column * cell, _horizon - height + _h * 0.012 + row * _h * 0.016, _h * 0.0045, _h * 0.0062));
                                }
                            }
                        }
                    }
                    x += width + (random.NextDouble() < 0.3 ? _h * 0.004 : 0);
                }
                AddRect(b, new Rect(tallest.X + tallest.Width / 2 - _h * 0.0015, tallest.Top - _h * 0.035, _h * 0.003, _h * 0.036));
            }
            _skyline = buildings;
            _windows = windows;

            var mid = new StreamGeometry();
            using (var m = mid.Open())
            {
                m.SetFillRule(FillRule.NonZero);
                var rnd = new Random(7);
                foreach (var (fx, fh) in new[] { (0.47, 0.11), (0.505, 0.135), (0.58, 0.10) })
                {
                    AddPalm(m, new Point(_w * fx, _horizon + _h * 0.035), _h * fh, _h * 0.008, _h * 0.045, _h * 0.0035, _h * 0.0022, rnd);
                }
            }
            _midPalms = mid;
            _ground = new Rect(0, _horizon, _w, _h - _horizon);
        }

        private void BuildFreeway()
        {
            var deckTop = _horizon + _h * 0.05;
            var thickness = _h * 0.026;
            _deckTop = deckTop;
            var freeway = new StreamGeometry();
            using (var f = freeway.Open())
            {
                f.SetFillRule(FillRule.NonZero);
                AddRect(f, new Rect(0, deckTop, _w, thickness));
                AddRect(f, new Rect(0, deckTop - _h * 0.011, _w, _h * 0.003));
                for (var x = 0.0; x < _w; x += _h * 0.032)
                {
                    AddRect(f, new Rect(x, deckTop - _h * 0.011, Math.Max(1, _h * 0.0022), _h * 0.011));
                }
                var pillarWidth = _h * 0.034;
                for (var x = _w * 0.05; x < _w; x += _w * 0.17)
                {
                    var top = deckTop + thickness - 1;
                    f.BeginFigure(new Point(x - pillarWidth * 0.5, top), true);
                    f.LineTo(new Point(x + pillarWidth * 0.5, top));
                    f.LineTo(new Point(x + pillarWidth * 0.64, _h));
                    f.LineTo(new Point(x - pillarWidth * 0.64, _h));
                    f.EndFigure(true);
                }
            }
            _freeway = freeway;

            var posts = new StreamGeometry();
            using (var p = posts.Open())
            {
                for (var x = _w * 0.03; x < _w; x += _w * 0.14)
                {
                    var bottom = deckTop - _h * 0.011;
                    var top = deckTop - _h * 0.115;
                    p.BeginFigure(new Point(x, bottom), false);
                    p.LineTo(new Point(x, top));
                    p.QuadraticBezierTo(new Point(x + _h * 0.002, top - _h * 0.012), new Point(x + _h * 0.03, top - _h * 0.006));
                    p.EndFigure(false);
                    _lampHeads.Add(new Point(x + _h * 0.03, top - _h * 0.003));
                }
            }
            _posts = posts;
            _postPen = new Pen(new SolidColorBrush(Color.FromRgb(0x14, 0x0B, 0x17)), Math.Max(1.5, _h * 0.0045), lineCap: PenLineCap.Round);
        }

        private void BuildPalms()
        {
            var palms = new StreamGeometry();
            using (var c = palms.Open())
            {
                c.SetFillRule(FillRule.NonZero);
                var random = new Random(24);
                AddPalm(c, new Point(_w * 0.07, _h * 1.02), _h * 0.9, _h * 0.1, _h * 0.2, _h * 0.02, _h * 0.011, random);
                AddPalm(c, new Point(_w * 0.935, _h * 1.03), _h * 0.7, -_h * 0.08, _h * 0.17, _h * 0.017, _h * 0.0095, random);
                AddPalm(c, new Point(_w * 0.865, _h * 1.04), _h * 0.5, -_h * 0.025, _h * 0.13, _h * 0.014, _h * 0.008, random);
            }
            _palms = palms;
        }

        public void Draw(DrawingContext context, double warmup)
        {
            context.DrawRectangle(_sky, null, _skyRect);

            var glowRadius = _sunRadius * 3.2;
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 0xFF, 0xB4, 0x5C), 0),
                    new GradientStop(Color.FromArgb(60, 0xFF, 0x8A, 0x4D), 0.4),
                    new GradientStop(Color.FromArgb(0, 0xFF, 0x8A, 0x4D), 1),
                },
            }, null, _sunCenter, glowRadius, glowRadius);

            context.DrawEllipse(new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.45, 0.35, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xFF, 0xF4, 0xD0), 0),
                    new GradientStop(Color.FromRgb(0xFF, 0xD2, 0x7A), 0.55),
                    new GradientStop(Color.FromRgb(0xFF, 0x9F, 0x4E), 1),
                },
            }, null, _sunCenter, _sunRadius, _sunRadius);

            foreach (var (rect, brush) in _haze) context.DrawRectangle(brush, null, rect);
            context.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0xFF, 0xB1, 0x5C), 0),
                    new GradientStop(Color.FromArgb(90, 0xFF, 0xB1, 0x5C), 1),
                },
            }, null, _horizonGlow);

            context.DrawGeometry(new SolidColorBrush(Color.FromArgb(235, 0x58, 0x29, 0x50)), null, _farRidge);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x36, 0x19, 0x3B)), null, _nearRidge);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x24, 0x11, 0x29)), null, _skyline);
            context.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(120 + 90 * warmup), 0xFF, 0xC2, 0x7A)), null, _windows);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1E, 0x10, 0x24)), null, _midPalms);

            context.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x1D, 0x10, 0x23), 0),
                    new GradientStop(Color.FromRgb(0x0C, 0x08, 0x0F), 1),
                },
            }, null, _ground);

            var lamp = Lerp(ColdLamp, Sodium, warmup);
            var intensity = 0.25 + 0.75 * warmup;

            foreach (var head in _lampHeads)
            {
                var cone = new StreamGeometry();
                using (var c = cone.Open())
                {
                    c.BeginFigure(new Point(head.X - _h * 0.006, head.Y), true);
                    c.LineTo(new Point(head.X + _h * 0.006, head.Y));
                    c.LineTo(new Point(head.X + _h * 0.05, _deckTop));
                    c.LineTo(new Point(head.X - _h * 0.05, _deckTop));
                    c.EndFigure(true);
                }
                context.DrawGeometry(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(WithAlpha(lamp, 0.20 * intensity), 0),
                        new GradientStop(WithAlpha(lamp, 0.02), 1),
                    },
                }, null, cone);
            }

            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x13, 0x0A, 0x16)), null, _freeway);
            context.DrawGeometry(null, _postPen, _posts);

            var glow = _h * 0.07;
            foreach (var head in _lampHeads)
            {
                context.DrawEllipse(new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(WithAlpha(lamp, 0.55 * intensity), 0),
                        new GradientStop(WithAlpha(lamp, 0.16 * intensity), 0.35),
                        new GradientStop(WithAlpha(lamp, 0), 1),
                    },
                }, null, head, glow, glow);
                context.DrawEllipse(new SolidColorBrush(Lerp(lamp, Color.FromRgb(0xFF, 0xE6, 0xB8), warmup * 0.7)), null, head, _h * 0.0065, _h * 0.0045);
            }

            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x0A, 0x06, 0x0E)), null, _palms);
        }

        private static void AddPalm(StreamGeometryContext c, Point basePoint, double height, double lean, double crown, double trunkBase, double trunkTop, Random random)
        {
            var top = new Point(basePoint.X + lean, basePoint.Y - height);
            var control = new Point(basePoint.X + lean * 0.12, basePoint.Y - height * 0.55);
            const int segments = 20;
            var left = new List<Point>(segments + 1);
            var right = new List<Point>(segments + 1);
            for (var i = 0; i <= segments; i++)
            {
                var t = i / (double)segments;
                var p = Quad(basePoint, control, top, t);
                var normal = Normal(QuadDerivative(basePoint, control, top, t));
                var width = trunkBase + (trunkTop - trunkBase) * t;
                if (t < 0.07) width *= 1 + (0.07 - t) * 6;
                left.Add(p + normal * width);
                right.Add(p - normal * width);
            }
            c.BeginFigure(left[0], true);
            foreach (var point in left.Skip(1)) c.LineTo(point);
            for (var i = right.Count - 1; i >= 0; i--) c.LineTo(right[i]);
            c.EndFigure(true);

            (double Angle, double Scale)[] fronds =
            [
                (172, 1.0), (147, 0.95), (122, 0.84), (98, 0.6), (76, 0.8), (52, 0.95), (24, 1.0),
                (198, 0.78), (-10, 0.8), (230, 0.55), (-50, 0.55),
            ];
            foreach (var (angle, scale) in fronds)
            {
                var radians = (angle + (random.NextDouble() - 0.5) * 10) * Math.PI / 180;
                var direction = new Vector(Math.Cos(radians), -Math.Sin(radians));
                var length = crown * scale * (0.92 + random.NextDouble() * 0.16);
                var droop = length * (0.28 + 0.27 * (1 - Math.Abs(Math.Sin(radians))));
                var end = top + direction * length + new Vector(0, droop);
                var mid = top + direction * (length * 0.55) + new Vector(0, -length * 0.1);
                AddLeaf(c, top, mid, end, length * 0.085);
            }

            var knob = crown * 0.06;
            c.BeginFigure(new Point(top.X - knob, top.Y), true);
            c.LineTo(new Point(top.X, top.Y - knob * 0.8));
            c.LineTo(new Point(top.X + knob, top.Y));
            c.LineTo(new Point(top.X, top.Y + knob * 1.4));
            c.EndFigure(true);
        }

        private static void AddLeaf(StreamGeometryContext c, Point start, Point control, Point end, double maxHalfWidth)
        {
            const int segments = 18;
            var upper = new List<Point>(segments + 1);
            var lower = new List<Point>(segments + 1);
            for (var i = 0; i <= segments; i++)
            {
                var t = i / (double)segments;
                var p = Quad(start, control, end, t);
                var normal = Normal(QuadDerivative(start, control, end, t));
                var width = maxHalfWidth * Math.Pow(Math.Sin(Math.PI * Math.Min(1, t * 1.12)), 0.7) * (1 - 0.45 * t);
                if (i % 2 == 1) width *= 0.5; // leaflet notches
                upper.Add(p + normal * width);
                lower.Add(p - normal * width * 0.7);
            }
            c.BeginFigure(upper[0], true);
            foreach (var point in upper.Skip(1)) c.LineTo(point);
            for (var i = lower.Count - 1; i >= 0; i--) c.LineTo(lower[i]);
            c.EndFigure(true);
        }

        private static StreamGeometry Polygon(IReadOnlyList<Point> points)
        {
            var geometry = new StreamGeometry();
            using var c = geometry.Open();
            c.BeginFigure(points[0], true);
            for (var i = 1; i < points.Count; i++) c.LineTo(points[i]);
            c.EndFigure(true);
            return geometry;
        }

        private static void AddRect(StreamGeometryContext c, Rect rect)
        {
            c.BeginFigure(rect.TopLeft, true);
            c.LineTo(rect.TopRight);
            c.LineTo(rect.BottomRight);
            c.LineTo(rect.BottomLeft);
            c.EndFigure(true);
        }

        private static Point Quad(Point a, Point b, Point c, double t)
        {
            var u = 1 - t;
            return new Point(u * u * a.X + 2 * u * t * b.X + t * t * c.X, u * u * a.Y + 2 * u * t * b.Y + t * t * c.Y);
        }

        private static Vector QuadDerivative(Point a, Point b, Point c, double t)
            => new(2 * (1 - t) * (b.X - a.X) + 2 * t * (c.X - b.X), 2 * (1 - t) * (b.Y - a.Y) + 2 * t * (c.Y - b.Y));

        private static Vector Normal(Vector d)
        {
            var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            return length < 1e-9 ? new Vector(1, 0) : new Vector(-d.Y / length, d.X / length);
        }

        private static Color Lerp(Color a, Color b, double t)
            => Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));

        private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), color.R, color.G, color.B);
    }
}
