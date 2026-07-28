using System.Drawing.Drawing2D;

namespace QuickHue;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore _store;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _onItem;
    private readonly ToolStripMenuItem _offItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly Control _dispatcher;
    private readonly Dictionary<HueIconState, Icon> _icons = [];
    private AppConfig _config;
    private HueController? _controller;
    private HueConnectionState _state = HueConnectionState.Offline;
    private bool _settingsOpen;

    public TrayApplicationContext(ConfigStore store, AppConfig config)
    {
        _store = store;
        _config = config;
        _dispatcher = new Control();
        _dispatcher.CreateControl();
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.Pressed += (_, _) => Toggle();

        foreach (var state in Enum.GetValues<HueIconState>())
        {
            _icons[state] = IconFactory.Create(state, SystemInformation.SmallIconSize.Width);
        }

        _statusItem = new ToolStripMenuItem("Not set up yet") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Toggle", null, (_, _) => Toggle());
        _onItem = new ToolStripMenuItem("Turn on", null, (_, _) => SetPower(true));
        _offItem = new ToolStripMenuItem("Turn off", null, (_, _) => SetPower(false));
        _startupItem = new ToolStripMenuItem("Start with Windows") { Checked = config.StartWithWindows };
        _startupItem.Click += (_, _) => ToggleStartup();

        _menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = true,
            DropShadowEnabled = true,
            Font = Theme.Text(9.5F),
            Padding = new Padding(0, 5, 0, 5)
        };
        _menu.Items.AddRange(
        [
            _statusItem,
            new ToolStripSeparator(),
            _toggleItem,
            _onItem,
            _offItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit QuickHue", null, (_, _) => ExitThread())
        ]);
        foreach (var item in _menu.Items.OfType<ToolStripMenuItem>())
        {
            item.Padding = new Padding(2, 5, 2, 5);
        }
        _menu.Opening += (_, _) => RefreshMenu();
        _menu.Resize += (_, _) => ApplyMenuShape();
        _menu.HandleCreated += (_, _) => ApplyMenuShape();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icons[HueIconState.Offline],
            Text = "QuickHue — not set up yet",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                Toggle();
            }
        };
        _notifyIcon.BalloonTipClicked += (_, _) => ShowSettings();

        Theme.Changed += OnThemeChanged;

        if (_config.IsConfigured)
        {
            ApplyConfiguration();
        }
        else
        {
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                ShowSettings();
            };
            timer.Start();
        }
    }

    private void ApplyConfiguration()
    {
        _controller?.Dispose();
        _controller = null;
        _config = _store.Load();
        _startupItem.Checked = _config.StartWithWindows;
        try
        {
            StartupManager.SetEnabled(_config.StartWithWindows);
        }
        catch (Exception exception)
        {
            ShowError($"Could not update Windows startup: {exception.Message}");
        }

        if (!_hotkeyWindow.TryRegister(_config.Hotkey, out var hotkeyError))
        {
            ShowError($"The {_config.Hotkey.DisplayText} hotkey is already in use. Tray controls still work — open Settings to pick another. ({hotkeyError})");
        }

        RefreshMenu();

        try
        {
            _controller = new HueController(_config, _store);
            _controller.StateChanged += (_, state) => Dispatch(() => UpdateState(state));
            _controller.Error += (_, message) => Dispatch(() => ShowError(message));
            _ = _controller.StartAsync();
        }
        catch (Exception exception)
        {
            UpdateState(HueConnectionState.Offline);
            ShowError(HueController.FriendlyMessage(exception));
        }
    }

    private void Toggle()
    {
        if (_controller is null)
        {
            ShowSettings();
            return;
        }
        _ = _controller.ToggleAsync();
    }

    private void SetPower(bool isOn)
    {
        if (_controller is null)
        {
            ShowSettings();
            return;
        }
        _ = _controller.SetAsync(isOn);
    }

    private void ShowSettings()
    {
        if (_settingsOpen)
        {
            return;
        }

        _settingsOpen = true;
        _hotkeyWindow.Unregister();
        _controller?.Dispose();
        _controller = null;
        try
        {
            using var form = new SetupForm(_store, _store.Load());
            if (form.ShowDialog() == DialogResult.OK || _store.Load().IsConfigured)
            {
                ApplyConfiguration();
            }
        }
        finally
        {
            _settingsOpen = false;
        }
    }

    private void ToggleStartup()
    {
        if (!_config.IsConfigured)
        {
            ShowSettings();
            return;
        }
        try
        {
            _config.StartWithWindows = !_config.StartWithWindows;
            StartupManager.SetEnabled(_config.StartWithWindows);
            _store.Save(_config);
            _startupItem.Checked = _config.StartWithWindows;
        }
        catch (Exception exception)
        {
            ShowError($"Could not update Windows startup: {exception.Message}");
        }
    }

    private void UpdateState(HueConnectionState state)
    {
        _state = state;
        var name = TargetName();
        var (icon, label) = state switch
        {
            HueConnectionState.On => (HueIconState.On, "On"),
            HueConnectionState.Off => (HueIconState.Off, "Off"),
            HueConnectionState.Connecting => (HueIconState.Connecting, "Connecting…"),
            _ => (HueIconState.Offline, "Unreachable")
        };
        _notifyIcon.Icon = _icons[icon];
        _notifyIcon.Text = TrimTooltip($"QuickHue — {name}: {label}");
        RefreshMenu();
    }

    /// <summary>Keeps menu labels, check marks, and the shortcut hint in step with the light.</summary>
    private void RefreshMenu()
    {
        var configured = _config.IsConfigured;
        var name = TargetName();
        _statusItem.Text = configured
            ? _state switch
            {
                HueConnectionState.On => $"{name} — On",
                HueConnectionState.Off => $"{name} — Off",
                HueConnectionState.Connecting => $"{name} — connecting…",
                _ => $"{name} — unreachable"
            }
            : "Not set up yet";

        _toggleItem.ShortcutKeyDisplayString = configured ? _config.Hotkey.DisplayText : string.Empty;
        _toggleItem.ShowShortcutKeys = configured;
        _toggleItem.Enabled = configured;
        _onItem.Enabled = configured;
        _offItem.Enabled = configured;
        _onItem.Checked = configured && _state == HueConnectionState.On;
        _offItem.Checked = configured && _state == HueConnectionState.Off;
        _startupItem.Checked = _config.StartWithWindows;
    }

    private string TargetName() =>
        string.IsNullOrWhiteSpace(_config.Target.Name) ? "Your light" : _config.Target.Name;

    private void ApplyMenuShape()
    {
        if (!_menu.IsHandleCreated || _menu.Width <= 0 || _menu.Height <= 0)
        {
            return;
        }
        using var path = Theme.RoundedRectangle(new Rectangle(0, 0, _menu.Width, _menu.Height), 8);
        _menu.Region?.Dispose();
        _menu.Region = new Region(path);
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs) =>
        Dispatch(() =>
        {
            _menu.Font = Theme.Text(9.5F);
            _menu.Invalidate();
        });

    private void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "QuickHue";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.IsDisposed)
        {
            return;
        }
        if (_dispatcher.InvokeRequired)
        {
            _dispatcher.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static string TrimTooltip(string text) => text.Length <= 63 ? text : text[..63];

    protected override void ExitThreadCore()
    {
        Theme.Changed -= OnThemeChanged;
        _controller?.Dispose();
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _dispatcher.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        base.ExitThreadCore();
    }

    /// <summary>Paints the tray menu with QuickHue's palette instead of the legacy grey chrome.</summary>
    private sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayMenuColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
        {
            eventArgs.Graphics.Clear(Theme.Colors.Card);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.StrokeRounded(
                eventArgs.Graphics,
                Theme.Strokeable(eventArgs.AffectedBounds),
                8,
                Theme.Colors.CardBorder);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs eventArgs)
        {
            if (!eventArgs.Item.Selected || !eventArgs.Item.Enabled)
            {
                return;
            }
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(4, 0, eventArgs.Item.Width - 8, eventArgs.Item.Height - 1);
            Theme.FillRounded(eventArgs.Graphics, bounds, 5, Theme.Colors.Hover);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
        {
            var palette = Theme.Colors;
            eventArgs.TextColor = eventArgs.Item.Enabled ? palette.Ink : palette.Muted;
            base.OnRenderItemText(eventArgs);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs eventArgs)
        {
            var graphics = eventArgs.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = eventArgs.ImageRectangle;
            var box = new Rectangle(
                bounds.Left + bounds.Width / 2 - 7,
                bounds.Top + bounds.Height / 2 - 7,
                14,
                14);
            Theme.FillRounded(graphics, box, 4, Theme.Colors.Accent);
            using var tick = new Pen(Theme.Colors.AccentInk, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLines(tick,
            [
                new PointF(box.Left + 3.5F, box.Top + 7F),
                new PointF(box.Left + 6F, box.Top + 9.5F),
                new PointF(box.Left + 10.5F, box.Top + 4.5F)
            ]);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
        {
            using var pen = new Pen(Theme.Colors.CardBorder);
            var y = eventArgs.Item.Height / 2;
            eventArgs.Graphics.DrawLine(pen, 10, y, eventArgs.Item.Width - 10, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs eventArgs)
        {
            // The palette already fills the whole drop-down; no separate margin strip.
        }
    }

    private sealed class TrayMenuColors : ProfessionalColorTable
    {
        public TrayMenuColors() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Theme.Colors.Card;
        public override Color MenuBorder => Theme.Colors.CardBorder;
        public override Color MenuItemBorder => Theme.Colors.Hover;
        public override Color MenuItemSelected => Theme.Colors.Hover;
        public override Color MenuItemSelectedGradientBegin => Theme.Colors.Hover;
        public override Color MenuItemSelectedGradientEnd => Theme.Colors.Hover;
        public override Color MenuItemPressedGradientBegin => Theme.Colors.Press;
        public override Color MenuItemPressedGradientMiddle => Theme.Colors.Press;
        public override Color MenuItemPressedGradientEnd => Theme.Colors.Press;
        public override Color ImageMarginGradientBegin => Theme.Colors.Card;
        public override Color ImageMarginGradientMiddle => Theme.Colors.Card;
        public override Color ImageMarginGradientEnd => Theme.Colors.Card;
        public override Color SeparatorDark => Theme.Colors.CardBorder;
        public override Color SeparatorLight => Theme.Colors.CardBorder;
        public override Color CheckBackground => Theme.Colors.Accent;
        public override Color CheckSelectedBackground => Theme.Colors.Accent;
        public override Color CheckPressedBackground => Theme.Colors.AccentPress;
    }
}
