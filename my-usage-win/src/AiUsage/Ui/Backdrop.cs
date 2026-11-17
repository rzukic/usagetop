using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AiUsage.Ui;

/// <summary>
/// Windows 11's own glass: the Acrylic backdrop a flyout gets from the system,
/// with dark mode and rounded corners from the window manager. Windows has no
/// Liquid Glass; this is its equivalent, and like the Mac panel it carries the
/// Glass setting as a layer of the window colour over it.
///
/// The backdrop needs the window to stay non-layered (no AllowsTransparency)
/// with a transparent client area and the frame extended over all of it.
/// Before Windows 11 22H2 there is no backdrop to ask for, and the window keeps
/// a solid background instead.
/// </summary>
public static class Backdrop
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int SystemBackdropType = 38;

    private const int CornerRound = 2;
    private const int BackdropTransient = 3; // Acrylic, as flyouts and menus have it
    private const int BackdropMica = 2;      // Mica, for a main window

    /// <summary>True on Windows 11 22H2 and later, where a backdrop can be asked for.</summary>
    public static bool Supported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Applies the backdrop once the window has a handle; returns whether it took.</summary>
    public static bool Apply(Window window, bool acrylic)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        var dark = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        var corner = CornerRound;
        DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));

        if (!Supported) return false;

        // The client area has to be see-through for the backdrop to show, both to
        // WPF (a transparent composition background) and to the window manager
        // (the frame extended over the whole window).
        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        var type = acrylic ? BackdropTransient : BackdropMica;
        return DwmSetWindowAttribute(handle, SystemBackdropType, ref type, sizeof(int)) == 0;
    }

    /// <summary>
    /// The backdrop as Windows Terminal treats it: the desktop behind, blurred,
    /// and no tint from the system, so the window lays its own over it. The
    /// system's flyout Acrylic carries a grey tint and luminosity layer of its own
    /// that cannot be changed, and it greys out whatever colour is behind it;
    /// this keeps the colour and leaves how dark the glass is to the Glass setting.
    ///
    /// The blur is the accent policy's (SetWindowCompositionAttribute), which is
    /// undocumented but has carried the shell's own blurred surfaces since
    /// Windows 10. Returns false where it is not there, and the caller keeps a
    /// solid panel.
    /// </summary>
    public static bool ApplyBlur(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        var dark = 1;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int));
        var corner = CornerRound;
        DwmSetWindowAttribute(handle, WindowCornerPreference, ref corner, sizeof(int));

        if (HwndSource.FromHwnd(handle) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        if (Supported)
        {
            var none = BackdropNone;
            DwmSetWindowAttribute(handle, SystemBackdropType, ref none, sizeof(int));
        }

        // A near-transparent gradient colour: the accent wants one, and the tint
        // is the window's own to draw.
        var accent = new AccentPolicy { AccentState = AccentAcrylicBlurBehind, GradientColor = 0x01000000 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, false);
            var data = new CompositionAttributeData { Attribute = AttributeAccentPolicy, Data = pointer, SizeOfData = size };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const int BackdropNone = 1;
    private const int AccentAcrylicBlurBehind = 4;
    private const int AttributeAccentPolicy = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
