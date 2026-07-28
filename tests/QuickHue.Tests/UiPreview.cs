using System.Reflection;
using System.Runtime.InteropServices;
using QuickHue;

namespace QuickHue.Tests;

/// <summary>
/// Renders the settings window to PNG files so UI changes can be reviewed without
/// a Hue Bridge. Invoked with <c>--ui-preview [outputDirectory]</c>.
/// </summary>
internal static class UiPreview
{
    private const int PwRenderFullContent = 2;

    public static void Render(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var thread = new Thread(() => RenderCore(outputDirectory));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void RenderCore(string outputDirectory)
    {
        // Same setup as ApplicationConfiguration.Initialize(), spelled out because both
        // assemblies generate that helper and the names collide.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        // Never touch the real configuration while previewing.
        var sandbox = Path.Combine(Path.GetTempPath(), "QuickHue.UiPreview");
        var store = new ConfigStore(sandbox);

        foreach (var dark in new[] { false, true })
        {
            SetDarkMode(dark);
            var suffix = dark ? "dark" : "light";
            Capture(Path.Combine(outputDirectory, $"setup-first-run-{suffix}.png"), store, seedReady: false);
            Capture(Path.Combine(outputDirectory, $"setup-ready-{suffix}.png"), store, seedReady: true);
            CaptureTrayMenu(Path.Combine(outputDirectory, $"tray-menu-{suffix}.png"));
        }

        SetDarkMode(false);
        CaptureIcons(Path.Combine(outputDirectory, "tray-icons.png"));

        Console.WriteLine($"Wrote UI previews to {outputDirectory}");
    }

    private static void Capture(string path, ConfigStore store, bool seedReady)
    {
        var config = new AppConfig();
        using var form = new SetupForm(store, config);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(40, 40);
        form.Show();
        Pump(700);

        if (seedReady)
        {
            SeedReadyState(form);
            Pump(400);
        }

        Save(form, path);
        form.Hide();
        Pump(80);
    }

    /// <summary>Sheet of every tray state at 16, 20, 24, and 32 px, shown at 1x and 4x.</summary>
    private static void CaptureIcons(string path)
    {
        int[] sizes = [16, 20, 24, 32];
        var states = Enum.GetValues(typeof(HueIconState)).Cast<object>().ToArray();
        const int Cell = 40;
        using var sheet = new Bitmap(Cell * sizes.Length * 2 + 120, Cell * states.Length + 24);
        using (var graphics = Graphics.FromImage(sheet))
        {
            graphics.Clear(Color.FromArgb(0xF4, 0xF6, 0xF9));
            using var label = new Font("Segoe UI", 8F);
            using var ink = new SolidBrush(Color.Black);
            for (var row = 0; row < states.Length; row++)
            {
                graphics.DrawString(states[row].ToString(), label, ink, 6, row * Cell + 26);
                for (var column = 0; column < sizes.Length; column++)
                {
                    var size = sizes[column];
                    using var icon = IconFactory.Create((HueIconState)states[row], size);
                    var x = 90 + column * Cell;
                    var y = row * Cell + 20;
                    graphics.DrawIcon(icon, new Rectangle(x, y, size, size));
                    // Same icon magnified so pixel-level damage is visible.
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    graphics.DrawImage(icon.ToBitmap(), 90 + (sizes.Length + column) * Cell + 20, y, size * 2, size * 2);
                }
            }
            graphics.DrawString("actual size", label, ink, 90, 4);
            graphics.DrawString("2x", label, ink, 90 + sizes.Length * Cell + 20, 4);
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// Mirrors the tray menu so its custom renderer can be reviewed. The live menu is
    /// built inside TrayApplicationContext, which cannot run here without touching the
    /// real registry and tray.
    /// </summary>
    private static void CaptureTrayMenu(string path)
    {
        var rendererType = typeof(SetupForm).Assembly
            .GetType("QuickHue.TrayApplicationContext+TrayMenuRenderer")!;
        var themeType = typeof(SetupForm).Assembly.GetType("QuickHue.Theme")!;
        var font = (Font)themeType.GetMethod("Text", [typeof(float), typeof(FontStyle)])!
            .Invoke(null, [9.5F, FontStyle.Regular])!;

        using var menu = new ContextMenuStrip
        {
            Renderer = (ToolStripRenderer)Activator.CreateInstance(rendererType, nonPublic: true)!,
            ShowImageMargin = true,
            DropShadowEnabled = false,
            Font = font,
            Padding = new Padding(0, 5, 0, 5)
        };
        var toggle = new ToolStripMenuItem("Toggle")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+L",
            ShowShortcutKeys = true
        };
        menu.Items.AddRange(
        [
            new ToolStripMenuItem("Desk lamp — On") { Enabled = false },
            new ToolStripSeparator(),
            toggle,
            new ToolStripMenuItem("Turn on") { Checked = true },
            new ToolStripMenuItem("Turn off"),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…"),
            new ToolStripMenuItem("Start with Windows") { Checked = true },
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit QuickHue")
        ]);
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            item.Padding = new Padding(2, 5, 2, 5);
        }

        menu.Show(new Point(60, 60));
        Pump(350);
        using var path2 = RoundedPath(menu.Width, menu.Height);
        menu.Region = new Region(path2);
        menu.Invalidate(true);
        Pump(200);
        SaveWindow(menu.Handle, path);
        menu.Close();
        Pump(80);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(int width, int height)
    {
        var themeType = typeof(SetupForm).Assembly.GetType("QuickHue.Theme")!;
        return (System.Drawing.Drawing2D.GraphicsPath)themeType
            .GetMethod("RoundedRectangle")!
            .Invoke(null, [new Rectangle(0, 0, width, height), 8])!;
    }

    /// <summary>
    /// Fills the form with plausible bridge and light data so the finished state can be
    /// reviewed. Uses reflection because these are private implementation details.
    /// </summary>
    private static void SeedReadyState(Form form)
    {
        var type = form.GetType();
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

        var draft = type.GetField("_draft", Instance)!.GetValue(form)!;
        var configType = draft.GetType();
        configType.GetProperty("BridgeId")!.SetValue(draft, "001788FFFE1A2B3C");
        configType.GetProperty("BridgeAddress")!.SetValue(draft, "192.0.2.42");
        configType.GetProperty("CertificateSha256")!.SetValue(draft, new string('a', 64));
        configType.GetProperty("ProtectedApplicationKey")!.SetValue(draft, "preview");

        var bridgeList = (ComboBox)type.GetField("_bridgeList", Instance)!.GetValue(form)!;
        bridgeList.Items.Clear();
        var bridgeInfo = typeof(SetupForm).Assembly.GetType("QuickHue.BridgeInfo")!;
        bridgeList.Items.Add(Activator.CreateInstance(bridgeInfo, "001788FFFE1A2B3C", "192.0.2.42", "mDNS")!);
        bridgeList.SelectedIndex = 0;

        var lightType = typeof(SetupForm).Assembly.GetType("QuickHue.HueLight")!;
        var lightList = (ComboBox)type.GetField("_lightList", Instance)!.GetValue(form)!;
        lightList.Items.Clear();
        lightList.Items.Add(Activator.CreateInstance(lightType, "3f9c1a", "Desk lamp", true)!);
        lightList.Items.Add(Activator.CreateInstance(lightType, "7b2d4e", "Monitor backlight", false)!);
        lightList.Items.Add(Activator.CreateInstance(lightType, "9e5f0c", "Ceiling", false)!);
        lightList.SelectedIndex = 0;

        type.GetMethod("RefreshDerivedState", Instance)!.Invoke(form, null);
        var statusKind = typeof(SetupForm).Assembly.GetType("QuickHue.StatusKind")!;
        type.GetMethod("SetStatus", Instance)!.Invoke(form,
        [
            "Paired securely. Ctrl+Alt+L will toggle Desk lamp.",
            Enum.Parse(statusKind, "Success")
        ]);
    }

    private static void SetDarkMode(bool dark)
    {
        var property = typeof(SetupForm).Assembly.GetType("QuickHue.Theme")!
            .GetProperty("IsDark", BindingFlags.Static | BindingFlags.Public)!;
        property.GetSetMethod(nonPublic: true)!.Invoke(null, [dark]);
    }

    private static void Save(Form form, string path) => SaveWindow(form.Handle, path);

    private static void SaveWindow(IntPtr window, string path)
    {
        GetWindowRect(window, out var rect);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                PrintWindow(window, hdc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, int flags);
}
