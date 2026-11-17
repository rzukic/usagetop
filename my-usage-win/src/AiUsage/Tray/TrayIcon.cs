using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AiUsage.Ui;

namespace AiUsage.Tray;

/// <summary>
/// The taskbar presence: a ring gauge whose fill is the combined level and whose
/// colour is its severity, so the tray answers "am I about to run out" without a
/// click. Left click opens the flyout; right click has the menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private IntPtr _handle;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Refresh", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "usagetop",
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) LeftClick?.Invoke();
        };

        Update(null);
    }

    public event Action? LeftClick;

    public event Action? OpenRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    /// <summary>Where the icon sits, so the flyout can open against it.</summary>
    public Point CursorPosition => Cursor.Position;

    /// <summary>
    /// <paramref name="attention"/> adds a dot to the corner: something needs the
    /// user, and the ring alone cannot say so.
    /// </summary>
    public void Update(float? percent, string? tooltip = null, bool attention = false, bool number = false)
    {
        var previous = _handle;
        using (var image = Draw(percent, attention, number)) _handle = image.GetHicon();

        _icon.Icon = Icon.FromHandle(_handle);
        _icon.Text = tooltip is { Length: > 0 and <= 63 } ? tooltip : "usagetop";

        if (previous != IntPtr.Zero) DestroyIcon(previous);
    }

    /// <summary>
    /// A 32px ring: a dim track with the level drawn over it, starting at twelve
    /// o'clock. Drawn at 4x and downsampled, because GDI+ arcs at 32px are ragged.
    /// </summary>
    /// <summary>The icon as it would be drawn; `--snapshot` saves these to check them.</summary>
    public static Bitmap Draw(float? percent, bool attention, bool number) =>
        number && percent != null ? DrawNumber(percent.Value, attention) : DrawRing(percent, attention);

    private static Bitmap DrawRing(float? percent, bool attention)
    {
        const int size = 32;
        const int scale = 4;

        using var bitmap = new Bitmap(size * scale, size * scale);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var inset = 3f * scale;
            var box = new RectangleF(
                inset,
                inset,
                size * scale - inset * 2,
                size * scale - inset * 2);

            using var track = new Pen(Color.FromArgb(90, 255, 255, 255), 3.4f * scale);
            graphics.DrawEllipse(track, box);

            if (percent is { } value)
            {
                var color = Severity.ColorFor(value);
                using var fill = new Pen(Color.FromArgb(color.R, color.G, color.B), 3.4f * scale)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };

                // A full ring would leave a seam where the caps meet, so 100% stops
                // a hair short of closing.
                var sweep = Math.Clamp(value, 0f, 100f) / 100f * 359.9f;
                if (sweep > 0.5f) graphics.DrawArc(fill, box, -90f, sweep);
            }

            if (attention)
            {
                // Bottom right, with a dark rim so it stays a dot over the ring
                // instead of merging into it.
                const float dot = 13f * scale;
                var corner = size * scale - dot;
                using var rim = new SolidBrush(Color.FromArgb(0x0A, 0x0A, 0x0A));
                using var mark = new SolidBrush(Color.FromArgb(0xE0, 0xA3, 0x3C));
                graphics.FillEllipse(rim, corner - scale, corner - scale, dot + scale, dot + scale);
                graphics.FillEllipse(mark, corner, corner, dot - scale, dot - scale);
            }
        }

        return new Bitmap(bitmap, new Size(size, size));
    }

    /// <summary>
    /// The level as a number, the tray's stand-in for the Mac's percentage beside
    /// its ring. Drawn at the size the tray shows (16px at 100%), not scaled down
    /// from 32 as the ring is, because downscaled digits blur. White while
    /// healthy, amber and red as it runs low, as the panel colours a figure.
    /// </summary>
    private static Bitmap DrawNumber(float percent, bool attention)
    {
        var size = SystemInformation.SmallIconSize.Width;
        var text = ((int)Math.Round(Math.Clamp(percent, 0f, 100f))).ToString();
        var severity = Severity.ColorFor(percent);
        var colour = percent > 40
            ? Color.FromArgb(0xF2, 0xF2, 0xF2)
            : Color.FromArgb(severity.R, severity.G, severity.B);

        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // As large as fits: two digits fill the square, "100" steps down.
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            // A pixel past the edge is fine; a wrapped line would draw nothing at all.
            format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            var em = size * 1.1f;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                if (graphics.MeasureString(text, font, size * 4, format).Width <= size + 1 || em <= size * 0.45f) break;
                font.Dispose();
                em -= 0.5f;
            }
            using (font)
            using (var brush = new SolidBrush(colour))
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size, size + size * 0.06f), format);
            }

            if (attention)
            {
                var dot = Math.Max(4f, size * 0.32f);
                using var rim = new SolidBrush(Color.FromArgb(0x0A, 0x0A, 0x0A));
                using var mark = new SolidBrush(Color.FromArgb(0xE0, 0xA3, 0x3C));
                graphics.FillEllipse(rim, size - dot - 1, -1, dot + 1, dot + 1);
                graphics.FillEllipse(mark, size - dot, 0, dot - 1, dot - 1);
            }
        }
        return bitmap;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
    }
}
