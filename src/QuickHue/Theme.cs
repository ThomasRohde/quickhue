using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuickHue;

internal sealed class Palette
{
    public required Color Canvas { get; init; }
    public required Color Card { get; init; }
    public required Color CardBorder { get; init; }
    public required Color Ink { get; init; }
    public required Color Muted { get; init; }
    public required Color Rail { get; init; }
    public required Color RailInk { get; init; }
    public required Color RailMuted { get; init; }
    public required Color RailDivider { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPress { get; init; }
    public required Color AccentInk { get; init; }
    public required Color Input { get; init; }
    public required Color InputBorder { get; init; }
    public required Color Hover { get; init; }
    public required Color Press { get; init; }
    public required Color Disabled { get; init; }
    public required Color DisabledInk { get; init; }
    public required Color Success { get; init; }
    public required Color SuccessFill { get; init; }
    public required Color Warning { get; init; }
    public required Color WarningFill { get; init; }
    public required Color Error { get; init; }
    public required Color ErrorFill { get; init; }
}

/// <summary>
/// Central palette, font, and window-chrome helpers so every surface follows the
/// Windows light/dark setting instead of hard-coding one look.
/// </summary>
internal static class Theme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int CornerPreferenceRound = 2;

    private static readonly ConcurrentDictionary<(string Family, float Size, FontStyle Style), Font> FontCache = new();
    private static readonly string[] DisplayFamilies = ["Segoe UI Variable Display", "Segoe UI"];
    private static readonly string[] TextFamilies = ["Segoe UI Variable Text", "Segoe UI"];
    private static readonly string[] MonoFamilies = ["Cascadia Mono", "Consolas", "Segoe UI"];
    private static string? _display;
    private static string? _text;
    private static string? _mono;

    private static readonly Palette LightPalette = new()
    {
        Canvas = Rgb(0xF4F6F9),
        Card = Color.White,
        CardBorder = Rgb(0xE1E6ED),
        Ink = Rgb(0x111827),
        Muted = Rgb(0x5C6675),
        Rail = Rgb(0x111827),
        RailInk = Color.White,
        RailMuted = Rgb(0xB6C0D0),
        RailDivider = Rgb(0x303A4B),
        Accent = Rgb(0xF6C344),
        AccentHover = Rgb(0xFFD263),
        AccentPress = Rgb(0xDEA624),
        AccentInk = Rgb(0x231A03),
        Input = Color.White,
        InputBorder = Rgb(0xD3DAE4),
        Hover = Rgb(0xF7F9FB),
        Press = Rgb(0xE9EDF3),
        Disabled = Rgb(0xEBEEF3),
        DisabledInk = Rgb(0x9AA2B0),
        Success = Rgb(0x0F7A5F),
        SuccessFill = Rgb(0xE7F6F1),
        Warning = Rgb(0x8A6100),
        WarningFill = Rgb(0xFEF6E0),
        Error = Rgb(0xB3323A),
        ErrorFill = Rgb(0xFDEDEE)
    };

    private static readonly Palette DarkPalette = new()
    {
        Canvas = Rgb(0x15171C),
        Card = Rgb(0x1E2128),
        CardBorder = Rgb(0x2F343E),
        Ink = Rgb(0xF1F3F7),
        Muted = Rgb(0x99A2B1),
        Rail = Rgb(0x0C0E12),
        RailInk = Color.White,
        RailMuted = Rgb(0x8B95A6),
        RailDivider = Rgb(0x272C36),
        Accent = Rgb(0xF6C344),
        AccentHover = Rgb(0xFFD263),
        AccentPress = Rgb(0xD9A32A),
        AccentInk = Rgb(0x231A03),
        Input = Rgb(0x272B34),
        InputBorder = Rgb(0x3A414E),
        Hover = Rgb(0x2A2F39),
        Press = Rgb(0x333944),
        Disabled = Rgb(0x22262E),
        DisabledInk = Rgb(0x646C7A),
        Success = Rgb(0x4ED8A2),
        SuccessFill = Rgb(0x152A23),
        Warning = Rgb(0xF0C566),
        WarningFill = Rgb(0x2B2415),
        Error = Rgb(0xF08A8A),
        ErrorFill = Rgb(0x2E1A1C)
    };

    static Theme()
    {
        IsDark = ReadSystemPrefersDark();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color
                or UserPreferenceCategory.VisualStyle))
            {
                return;
            }
            var dark = ReadSystemPrefersDark();
            if (dark == IsDark)
            {
                return;
            }
            IsDark = dark;
            Changed?.Invoke(null, EventArgs.Empty);
        };
    }

    /// <summary>Raised when Windows switches between light and dark app mode.</summary>
    public static event EventHandler? Changed;

    public static bool IsDark { get; private set; }

    public static Palette Colors => IsDark ? DarkPalette : LightPalette;

    public static Font Display(float size, FontStyle style = FontStyle.Regular) =>
        Resolve(_display ??= FirstInstalled(DisplayFamilies), size, style);

    public static Font Text(float size, FontStyle style = FontStyle.Regular) =>
        Resolve(_text ??= FirstInstalled(TextFamilies), size, style);

    public static Font Mono(float size, FontStyle style = FontStyle.Regular) =>
        Resolve(_mono ??= FirstInstalled(MonoFamilies), size, style);

    /// <summary>Paints the title bar to match the theme and rounds the corners on Windows 11.</summary>
    public static void ApplyWindowChrome(IWin32Window window)
    {
        if (window.Handle == IntPtr.Zero)
        {
            return;
        }
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(window.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var corner = CornerPreferenceRound;
        _ = DwmSetWindowAttribute(window.Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    /// <summary>
    /// Colour actually behind a control. Custom-painted controls sit on transparent
    /// layout panels, so <c>Parent.BackColor</c> alone would clear to black.
    /// </summary>
    public static Color Backdrop(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is CardPanel)
            {
                return Colors.Card;
            }
            if (parent.BackColor.A == 255)
            {
                return parent.BackColor;
            }
        }
        return Colors.Canvas;
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2)) * 2;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rectangle inset by one pixel so a 1px pen stays inside the control.</summary>
    public static Rectangle Strokeable(Rectangle bounds) => bounds with
    {
        Width = Math.Max(0, bounds.Width - 1),
        Height = Math.Max(0, bounds.Height - 1)
    };

    public static void FillRounded(Graphics graphics, Rectangle bounds, int radius, Color color)
    {
        using var path = RoundedRectangle(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics graphics, Rectangle bounds, int radius, Color color, float width = 1F)
    {
        using var path = RoundedRectangle(bounds, radius);
        using var pen = new Pen(color, width);
        graphics.DrawPath(pen, path);
    }

    public static void DrawChevron(Graphics graphics, Rectangle bounds, Color color)
    {
        var center = new PointF(bounds.Left + bounds.Width / 2F, bounds.Top + bounds.Height / 2F);
        using var pen = new Pen(color, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLines(pen,
        [
            new PointF(center.X - 4F, center.Y - 2F),
            new PointF(center.X, center.Y + 2.5F),
            new PointF(center.X + 4F, center.Y - 2F)
        ]);
    }

    private static Color Rgb(int value) =>
        Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

    private static Font Resolve(string family, float size, FontStyle style) =>
        FontCache.GetOrAdd((family, size, style), key => new Font(key.Family, key.Size, key.Style));

    private static string FirstInstalled(string[] families)
    {
        foreach (var family in families)
        {
            try
            {
                using var probe = new FontFamily(family);
                return family;
            }
            catch (ArgumentException)
            {
                // Not installed on this machine; fall through to the next candidate.
            }
        }
        return FontFamily.GenericSansSerif.Name;
    }

    private static bool ReadSystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
