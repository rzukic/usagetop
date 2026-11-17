using System.Windows;
using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// The rail's two marks, drawn rather than taken from the icon font: Segoe
/// Fluent Icons has them only as thin outlines, and beside the account rings
/// they want the weight of the Mac's filled symbols (square.grid.2x2.fill and
/// gearshape.fill). Both sit in a 16-unit box.
/// </summary>
public static class RailIcons
{
    /// <summary>Four rounded squares: the overview.</summary>
    public static Geometry Grid { get; } = BuildGrid();

    /// <summary>A gear with eight teeth and a hole: settings.</summary>
    public static Geometry Gear { get; } = BuildGear();

    private static Geometry BuildGrid()
    {
        var group = new GeometryGroup();
        foreach (var x in new[] { 1.0, 9.0 })
        {
            foreach (var y in new[] { 1.0, 9.0 })
            {
                group.Children.Add(new RectangleGeometry(new Rect(x, y, 6, 6), 1.8, 1.8));
            }
        }
        group.Freeze();
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
            context.BeginFigure(At(body, -baseAngle), true, true);
            for (var i = 0; i < teeth; i++)
            {
                var angle = i * 360.0 / teeth;
                context.LineTo(At(outer, angle - tipAngle), true, true);
                context.LineTo(At(outer, angle + tipAngle), true, true);
                context.LineTo(At(body, angle + baseAngle), true, true);
                var next = (i + 1) * 360.0 / teeth;
                context.ArcTo(At(body, next - baseAngle), new Size(body, body), 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        outline.Freeze();

        var gear = new GeometryGroup { FillRule = FillRule.EvenOdd };
        gear.Children.Add(outline);
        gear.Children.Add(new EllipseGeometry(centre, hole, hole));
        gear.Freeze();
        return gear;
    }
}
