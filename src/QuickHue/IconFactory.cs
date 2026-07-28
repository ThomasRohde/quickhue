using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace QuickHue;

internal enum HueIconState
{
    On,
    Off,
    Connecting,
    Offline
}

internal static class IconFactory
{
    /// <summary>
    /// Draws the tray glyph at the size Windows actually asks for, so the bulb stays
    /// crisp instead of being downsampled from a fixed 32 px bitmap.
    /// </summary>
    public static Icon Create(HueIconState state, int size = 32)
    {
        size = Math.Max(16, size);
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            Draw(graphics, state, size);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void Draw(Graphics graphics, HueIconState state, int size)
    {
        var scale = size / 32F;
        var accent = Color.FromArgb(0xF6, 0xC3, 0x44);
        var muted = Color.FromArgb(0x8A, 0x93, 0xA3);
        var offline = Color.FromArgb(0xE0, 0x5B, 0x5B);
        var color = state switch
        {
            HueIconState.On => accent,
            HueIconState.Offline => offline,
            _ => muted
        };

        // Laid out on a 32-unit grid: the bulb plus socket fill almost the whole box so
        // the glyph stays readable when the tray asks for 16 px.
        var center = new PointF(16F * scale, 13F * scale);
        var radius = 9.5F * scale;
        var bulb = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        if (state == HueIconState.On)
        {
            var halo = new RectangleF(center.X - 15.5F * scale, center.Y - 15.5F * scale, 31F * scale, 31F * scale);
            using var path = new GraphicsPath();
            path.AddEllipse(halo);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(110, accent),
                SurroundColors = [Color.FromArgb(0, accent)]
            };
            graphics.FillEllipse(glow, halo);
            using var fill = new SolidBrush(accent);
            graphics.FillEllipse(fill, bulb);
        }
        else
        {
            using var outline = new Pen(color, Math.Max(1.8F, 2.4F * scale));
            graphics.DrawEllipse(outline, RectangleF.Inflate(bulb, -1F * scale, -1F * scale));
        }

        using var socket = new SolidBrush(state == HueIconState.On ? Color.FromArgb(230, 108, 114, 126) : color);
        graphics.FillRectangle(socket, center.X - 5F * scale, center.Y + radius - 0.5F * scale, 10F * scale, 4F * scale);
        graphics.FillRectangle(socket, center.X - 3F * scale, center.Y + radius + 4.5F * scale, 6F * scale, 2F * scale);

        if (state == HueIconState.Offline)
        {
            using var strike = new Pen(offline, Math.Max(2F, 2.6F * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(strike, 5F * scale, 27F * scale, 27F * scale, 5F * scale);
        }
        else if (state == HueIconState.Connecting)
        {
            // Badge in the corner, clear of the socket.
            using var pending = new SolidBrush(accent);
            graphics.FillEllipse(pending, 22F * scale, 22F * scale, 9F * scale, 9F * scale);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
