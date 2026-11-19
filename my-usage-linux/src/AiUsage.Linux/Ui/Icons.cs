using Avalonia;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace AiUsage.Ui;

/// <summary>
/// The panel's glyphs as vectors, so it needs no icon font to be installed:
/// the rail's two marks as the Windows app draws them, the rest the 24-unit
/// Material shapes the Android app uses.
/// </summary>
public static class Icons
{
    public static Geometry Flame { get; } = Geometry.Parse(
        "M13.5,1.5s1,3.2 -1.4,6.1C10,10.2 7,11.6 7,15.5 7,19.1 9.7,22 13,22s6,-2.6 6,-6.4c0,-2.5 -1.1,-4.1 -2.1,-5.4 " +
        "-0.3,1.5 -1.2,2.6 -2.4,2.9 0.9,-3.6 0.3,-8.4 -1,-11.6z");

    public static Geometry Refresh { get; } = Geometry.Parse(
        "M17.65,6.35C16.2,4.9 14.21,4 12,4c-4.42,0 -7.99,3.58 -8,8s3.58,8 8,8c3.73,0 6.84,-2.55 7.73,-6h-2.08" +
        "c-0.82,2.33 -3.04,4 -5.65,4 -3.31,0 -6,-2.69 -6,-6s2.69,-6 6,-6c1.66,0 3.14,0.69 4.22,1.78L13,11h7V4l-2.35,2.35z");

    public static Geometry Chevron { get; } = Geometry.Parse(
        "M9.29,6.71a1,1 0,0 0,0 1.41L13.17,12l-3.88,3.88a1,1 0,1 0,1.42 1.41l4.58,-4.58a1,1 0,0 0,0 -1.42" +
        "L10.71,6.71a1,1 0,0 0,-1.42 0z");

    public static Geometry EyeOff { get; } = Geometry.Parse(
        "M12,7c2.76,0 5,2.24 5,5 0,0.65 -0.13,1.26 -0.36,1.83l2.92,2.92c1.51,-1.26 2.7,-2.89 3.43,-4.75 " +
        "-1.73,-4.39 -6,-7.5 -11,-7.5 -1.4,0 -2.74,0.25 -3.98,0.7l2.16,2.16C10.74,7.13 11.35,7 12,7z" +
        "M2,4.27l2.28,2.28 0.46,0.46C3.08,8.3 1.78,10.02 1,12c1.73,4.39 6,7.5 11,7.5 1.55,0 3.03,-0.3 4.38,-0.84" +
        "l0.42,0.42L19.73,22 21,20.73 3.27,3 2,4.27zM7.53,9.8l1.55,1.55c-0.05,0.21 -0.08,0.43 -0.08,0.65 " +
        "0,1.66 1.34,3 3,3 0.22,0 0.44,-0.03 0.65,-0.08l1.55,1.55c-0.67,0.33 -1.41,0.53 -2.2,0.53 " +
        "-2.76,0 -5,-2.24 -5,-5 0,-0.79 0.2,-1.53 0.53,-2.2zM11.84,9.02l3.15,3.15 0.02,-0.16c0,-1.66 -1.34,-3 -3,-3l-0.17,0.01z");

    /// <summary>A triangle with an exclamation mark cut out of it.</summary>
    public static Geometry Warning { get; } = Geometry.Parse(
        "M1,21h22L12,2 1,21zM13,18h-2v-2h2v2zM13,14h-2v-4h2v4z");

    /// <summary>A gauge with its needle: being spent, and lasting.</summary>
    public static Geometry Gauge { get; } = Geometry.Parse(
        "M20.38,8.57l-1.23,1.85a8,8 0,0 1,-0.22 7.58H5.07A8,8 0,0 1,15.58 6.85l1.85,-1.23A10,10 0,0 0,3.35 19" +
        "a2,2 0,0 0,1.72 1h13.85a2,2 0,0 0,1.74 -1 10,10 0,0 0,-0.27 -10.44zM10.59,15.41a2,2 0,0 0,2.83 0" +
        "l5.66,-8.49 -8.49,5.66a2,2 0,0 0,0 2.83z");

    /// <summary>A crescent: nothing is being spent.</summary>
    public static Geometry Moon { get; } = Geometry.Parse(
        "M12,3a9,9 0,1 0,9 9c0,-0.46 -0.04,-0.92 -0.1,-1.36a5.389,5.389 0,0 1,-4.4 2.26 5.403,5.403 0,0 1,-3.14 -9.8" +
        "c-0.44,-0.06 -0.9,-0.1 -1.36,-0.1z");

    /// <summary>A clock face: an earlier reading.</summary>
    public static Geometry Clock { get; } = Geometry.Parse(
        "M11.99,2C6.47,2 2,6.48 2,12s4.47,10 9.99,10C17.52,22 22,17.52 22,12S17.52,2 11.99,2zM12,20" +
        "c-4.42,0 -8,-3.58 -8,-8s3.58,-8 8,-8 8,3.58 8,8 -3.58,8 -8,8zM12.5,7H11v6l5.25,3.15 0.75,-1.23 -4.5,-2.67z");

    /// <summary>The power mark: a broken ring and a stroke through the gap.</summary>
    public static Geometry Power { get; } = Geometry.Parse(
        "M13,3h-2v10h2V3zM17.83,5.17l-1.42,1.42C17.99,7.86 19,9.81 19,12c0,3.87 -3.13,7 -7,7s-7,-3.13 -7,-7" +
        "c0,-2.19 1.01,-4.14 2.58,-5.42L6.17,5.17C4.23,6.82 3,9.26 3,12c0,4.97 4.03,9 9,9s9,-4.03 9,-9" +
        "c0,-2.74 -1.23,-5.18 -3.17,-6.83z");

    /// <summary>Four rounded squares: the overview. In a 16-unit box.</summary>
    public static Geometry Grid { get; } = BuildGrid();

    /// <summary>A gear with eight teeth and a hole: settings. In a 16-unit box.</summary>
    public static Geometry Gear { get; } = BuildGear();

    /// <summary>A glyph at a size, in one colour. The box is the glyph's own, 24 units unless said.</summary>
    public static Path Glyph(Geometry data, double size, IBrush brush) => new()
    {
        Data = data,
        Fill = brush,
        Stretch = Stretch.Uniform,
        Width = size,
        Height = size,
    };

    private static Geometry BuildGrid()
    {
        var group = new GeometryGroup { FillRule = FillRule.NonZero };
        foreach (var x in new[] { 1.0, 9.0 })
        {
            foreach (var y in new[] { 1.0, 9.0 })
            {
                group.Children.Add(new RectangleGeometry(new Rect(x, y, 6, 6), 1.8, 1.8));
            }
        }
        return group;
    }

    private static Geometry BuildGear()
    {
        const int teeth = 8;
        const double body = 5.5;      // radius between the teeth
        const double outer = 7.7;     // radius at a tooth's tip
        const double baseHalf = 1.55; // half a tooth's width where it meets the body
        const double tipHalf = 1.1;   // half its width at the tip
        const double hole = 2.3;
        var centre = new Point(8, 8);

        Point At(double radius, double degrees)
        {
            var radians = (degrees - 90) * Math.PI / 180;
            return new Point(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
        }

        var baseAngle = Math.Asin(baseHalf / body) * 180 / Math.PI;
        var tipAngle = Math.Asin(tipHalf / outer) * 180 / Math.PI;

        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(At(body, -baseAngle), true);
            for (var i = 0; i < teeth; i++)
            {
                var angle = i * 360.0 / teeth;
                context.LineTo(At(outer, angle - tipAngle));
                context.LineTo(At(outer, angle + tipAngle));
                context.LineTo(At(body, angle + baseAngle));
                var next = (i + 1) * 360.0 / teeth;
                context.ArcTo(At(body, next - baseAngle), new Size(body, body), 0, false, SweepDirection.Clockwise);
            }
            context.EndFigure(true);
        }

        var gear = new GeometryGroup { FillRule = FillRule.EvenOdd };
        gear.Children.Add(outline);
        gear.Children.Add(new EllipseGeometry { Center = centre, RadiusX = hole, RadiusY = hole });
        return gear;
    }
}
