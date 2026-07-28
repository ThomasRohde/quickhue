using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace QuickHue;

/// <summary>Rounded surface used to group related settings.</summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var bounds = Theme.Strokeable(ClientRectangle);
        Theme.FillRounded(graphics, bounds, 10, Theme.Colors.Card);
        Theme.StrokeRounded(graphics, bounds, 10, Theme.Colors.CardBorder);
    }
}

internal enum ButtonKind
{
    Primary,
    Secondary,
    Ghost
}

internal sealed class ThemedButton : Button
{
    private readonly ButtonKind _kind;
    private bool _hovered;
    private bool _pressed;

    public ThemedButton(ButtonKind kind)
    {
        _kind = kind;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Text(9F, FontStyle.Bold);
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(eventArgs);
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        if (!Enabled)
        {
            _hovered = false;
            _pressed = false;
        }
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnLostFocus(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var palette = Theme.Colors;
        var bounds = Theme.Strokeable(ClientRectangle);

        var (fill, ink, border) = Resolve(palette);
        if (fill.A > 0)
        {
            Theme.FillRounded(graphics, bounds, 7, fill);
        }
        if (border.A > 0)
        {
            Theme.StrokeRounded(graphics, bounds, 7, border);
        }

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            bounds,
            ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            Theme.StrokeRounded(graphics, Rectangle.Inflate(bounds, -3, -3), 5, ink, 1.4F);
        }
    }

    private (Color Fill, Color Ink, Color Border) Resolve(Palette palette)
    {
        if (!Enabled)
        {
            return _kind == ButtonKind.Ghost
                ? (Color.Transparent, palette.DisabledInk, Color.Transparent)
                : (palette.Disabled, palette.DisabledInk, Color.Transparent);
        }

        return _kind switch
        {
            ButtonKind.Primary => (
                _pressed ? palette.AccentPress : _hovered ? palette.AccentHover : palette.Accent,
                palette.AccentInk,
                Color.Transparent),
            ButtonKind.Secondary => (
                _pressed ? palette.Press : _hovered ? palette.Hover : palette.Input,
                palette.Ink,
                palette.InputBorder),
            _ => (
                _pressed ? palette.Press : _hovered ? palette.Hover : Color.Transparent,
                palette.Muted,
                Color.Transparent)
        };
    }
}

/// <summary>
/// Drop-down list that follows the theme. Windows paints combo chrome with system
/// colours, so the border and chevron are repainted after the default pass.
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private const int WmPaint = 0x000F;
    private const int ButtonWidth = 30;
    private Size _regionSize = Size.Empty;

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 24;
        Cursor = Cursors.Hand;
        Font = Theme.Text(9.5F);
    }

    /// <summary>Placeholder shown when the list is empty.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "None available";

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        ApplyTheme();
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        ApplyRegion();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Colors.Input;
        ForeColor = Theme.Colors.Ink;
        ApplyRegion();
        Invalidate();
    }

    /// <summary>
    /// Windows overrides the height of a drop-down list after layout, so the rounded
    /// clip has to be rebuilt from the size the control actually ended up with.
    /// </summary>
    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0 || _regionSize == Size)
        {
            return;
        }
        _regionSize = Size;
        using var path = Theme.RoundedRectangle(new Rectangle(0, 0, Width, Height), 6);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnDrawItem(DrawItemEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = Theme.Colors;
        var isEditRegion = (eventArgs.State & DrawItemState.ComboBoxEdit) != 0;
        var isSelected = !isEditRegion && (eventArgs.State & DrawItemState.Selected) != 0;

        using (var background = new SolidBrush(isSelected ? palette.Hover : palette.Input))
        {
            graphics.FillRectangle(background, eventArgs.Bounds);
        }

        if (eventArgs.Index < 0)
        {
            if (isEditRegion)
            {
                DrawText(graphics, EmptyText, eventArgs.Bounds, palette.Muted, 0);
            }
            return;
        }

        var item = Items[eventArgs.Index];
        var textInset = 0;
        if (item is HueLight light)
        {
            textInset = 18;
            var dot = new Rectangle(eventArgs.Bounds.Left + 6, eventArgs.Bounds.Top + (eventArgs.Bounds.Height - 8) / 2, 8, 8);
            if (light.IsOn)
            {
                using var fill = new SolidBrush(palette.Accent);
                graphics.FillEllipse(fill, dot);
            }
            else
            {
                using var pen = new Pen(palette.Muted, 1.3F);
                graphics.DrawEllipse(pen, dot);
            }
        }

        DrawText(graphics, item?.ToString() ?? string.Empty, eventArgs.Bounds, palette.Ink, textInset);

        if (isSelected)
        {
            using var marker = new SolidBrush(palette.Accent);
            graphics.FillRectangle(marker, eventArgs.Bounds.Left, eventArgs.Bounds.Top + 4, 2, eventArgs.Bounds.Height - 8);
        }
    }

    private void DrawText(Graphics graphics, string text, Rectangle bounds, Color color, int inset) =>
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            bounds with { X = bounds.X + 8 + inset, Width = Math.Max(0, bounds.Width - 12 - inset) },
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != WmPaint)
        {
            return;
        }

        ApplyRegion();
        using var graphics = Graphics.FromHwnd(Handle);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = Theme.Colors;
        var button = new Rectangle(Width - ButtonWidth, 0, ButtonWidth, Height);
        using (var eraser = new SolidBrush(Enabled ? palette.Input : palette.Disabled))
        {
            graphics.FillRectangle(eraser, button);
        }
        Theme.DrawChevron(graphics, button, Enabled ? palette.Muted : palette.DisabledInk);
        var border = Focused ? palette.Accent : palette.InputBorder;
        Theme.StrokeRounded(graphics, Theme.Strokeable(ClientRectangle), 6, border, Focused ? 1.6F : 1F);
    }
}

/// <summary>Rounded frame that hosts a borderless text box so inputs share one look.</summary>
internal sealed class InputFrame : Panel
{
    private readonly TextBox _inner;

    public InputFrame(TextBox inner)
    {
        _inner = inner;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Padding = new Padding(9, 0, 9, 0);
        _inner.BorderStyle = BorderStyle.None;
        _inner.Font = Theme.Text(9.5F);
        _inner.Dock = DockStyle.Fill;
        _inner.GotFocus += (_, _) => Invalidate();
        _inner.LostFocus += (_, _) => Invalidate();
        Controls.Add(_inner);
    }

    public void ApplyTheme()
    {
        _inner.BackColor = Theme.Colors.Input;
        _inner.ForeColor = Theme.Colors.Ink;
        Invalidate();
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        _inner.Focus();
        base.OnClick(eventArgs);
    }

    protected override void OnLayout(LayoutEventArgs eventArgs)
    {
        base.OnLayout(eventArgs);
        // Centre the single-line text box inside the frame.
        var top = Math.Max(0, (ClientSize.Height - _inner.PreferredHeight) / 2);
        _inner.Dock = DockStyle.None;
        _inner.SetBounds(Padding.Left, top, Math.Max(0, ClientSize.Width - Padding.Horizontal), _inner.PreferredHeight);
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var palette = Theme.Colors;
        var bounds = Theme.Strokeable(ClientRectangle);
        Theme.FillRounded(graphics, bounds, 6, palette.Input);
        var focused = _inner.Focused;
        Theme.StrokeRounded(graphics, bounds, 6, focused ? palette.Accent : palette.InputBorder, focused ? 1.6F : 1F);
    }
}

/// <summary>Pill-shaped on/off control used for "start with Windows".</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private const int TrackWidth = 42;
    private const int TrackHeight = 22;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private float _progress;
    private bool _hovered;

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = 26;
        Font = Theme.Text(9.5F);
        _animation.Tick += (_, _) =>
        {
            var target = Checked ? 1F : 0F;
            var delta = Math.Sign(target - _progress) * 0.16F;
            _progress = Math.Abs(target - _progress) <= 0.16F ? target : _progress + delta;
            if (_progress == target)
            {
                _animation.Stop();
            }
            Invalidate();
        };
    }

    protected override void OnCheckedChanged(EventArgs eventArgs)
    {
        base.OnCheckedChanged(eventArgs);
        if (IsHandleCreated)
        {
            _animation.Start();
        }
        else
        {
            _progress = Checked ? 1F : 0F;
        }
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnLostFocus(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var palette = Theme.Colors;
        var top = (Height - TrackHeight) / 2;
        var track = new Rectangle(0, top, TrackWidth - 1, TrackHeight - 1);

        var trackFill = Checked
            ? _hovered ? palette.AccentHover : palette.Accent
            : _hovered ? palette.Press : palette.Input;
        Theme.FillRounded(graphics, track, TrackHeight / 2, trackFill);
        if (!Checked)
        {
            Theme.StrokeRounded(graphics, track, TrackHeight / 2, palette.InputBorder);
        }

        var travel = TrackWidth - TrackHeight;
        var thumbX = track.Left + 3 + (int)Math.Round(travel * _progress);
        var thumbSize = TrackHeight - 7;
        var thumbColor = Checked ? palette.AccentInk : palette.Muted;
        using (var thumb = new SolidBrush(thumbColor))
        {
            graphics.FillEllipse(thumb, thumbX, track.Top + 3, thumbSize, thumbSize);
        }

        var textBounds = new Rectangle(TrackWidth + 12, 0, Math.Max(0, Width - TrackWidth - 12), Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textBounds,
            Enabled ? palette.Ink : palette.DisabledInk,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            Theme.StrokeRounded(graphics, Rectangle.Inflate(track, 3, 3), TrackHeight / 2 + 3, palette.Accent, 1.4F);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Records a global shortcut by listening for the next key press, and reports
/// whether Windows will actually hand that combination to QuickHue.
/// </summary>
internal sealed class HotkeyBox : Control
{
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;

    private HotkeyBinding _binding = HotkeyBinding.Default;
    private bool _recording;
    private bool _hovered;

    public HotkeyBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 40;
    }

    public event EventHandler? BindingChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyBinding Binding
    {
        get => _binding;
        set
        {
            _binding = value;
            Invalidate();
        }
    }

    public bool IsRecording => _recording;

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        Focus();
        StartRecording();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnGotFocus(EventArgs eventArgs)
    {
        Invalidate();
        base.OnGotFocus(eventArgs);
    }

    protected override void OnLostFocus(EventArgs eventArgs)
    {
        StopRecording();
        base.OnLostFocus(eventArgs);
    }

    public void StartRecording()
    {
        if (_recording)
        {
            return;
        }
        _recording = true;
        Invalidate();
    }

    public void StopRecording()
    {
        if (!_recording)
        {
            return;
        }
        _recording = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => _recording && (keyData & Keys.KeyCode) != Keys.Tab;

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        const int wmKeyDown = 0x0100;
        const int wmSysKeyDown = 0x0104;
        if (!_recording || (message.Msg != wmKeyDown && message.Msg != wmSysKeyDown))
        {
            return base.ProcessCmdKey(ref message, keyData);
        }

        var key = keyData & Keys.KeyCode;
        switch (key)
        {
            case Keys.Escape:
                StopRecording();
                return true;
            case Keys.Tab:
                StopRecording();
                return base.ProcessCmdKey(ref message, keyData);
            case Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin:
                Invalidate();
                return true;
        }

        var modifiers = CurrentModifiers();
        if (modifiers == 0)
        {
            Invalidate();
            return true;
        }
        if (!IsAssignableKey(key))
        {
            return true;
        }

        var candidate = new HotkeyBinding { Modifiers = modifiers, VirtualKey = (uint)key };
        if (!candidate.IsValid())
        {
            return true;
        }

        _binding = candidate;
        StopRecording();
        BindingChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        // Enter is left alone so it still triggers the form's default button.
        if (!_recording && eventArgs.KeyCode == Keys.Space)
        {
            StartRecording();
            eventArgs.Handled = true;
            return;
        }
        base.OnKeyDown(eventArgs);
    }

    protected override void OnKeyUp(KeyEventArgs eventArgs)
    {
        if (_recording)
        {
            Invalidate();
        }
        base.OnKeyUp(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var palette = Theme.Colors;
        var bounds = Theme.Strokeable(ClientRectangle);
        Theme.FillRounded(graphics, bounds, 7, _recording ? palette.Hover : palette.Input);
        var border = _recording || Focused ? palette.Accent : _hovered ? palette.Muted : palette.InputBorder;
        Theme.StrokeRounded(graphics, bounds, 7, border, _recording || Focused ? 1.6F : 1F);

        var parts = _recording ? LiveParts() : BindingParts();
        if (parts.Count == 0)
        {
            TextRenderer.DrawText(
                graphics,
                "Press a shortcut…",
                Theme.Text(9.5F),
                bounds with { X = bounds.X + 12, Width = bounds.Width - 12 },
                palette.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }

        var x = bounds.Left + 10;
        foreach (var part in parts)
        {
            x += DrawKeycap(graphics, part, x, bounds) + 6;
        }

        if (_recording)
        {
            TextRenderer.DrawText(
                graphics,
                "…",
                Theme.Text(11F),
                new Rectangle(x, bounds.Top, 24, bounds.Height),
                palette.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    private int DrawKeycap(Graphics graphics, string text, int x, Rectangle bounds)
    {
        var palette = Theme.Colors;
        var font = Theme.Mono(8.5F, FontStyle.Bold);
        var size = TextRenderer.MeasureText(graphics, text, font, new Size(200, 40), TextFormatFlags.NoPadding);
        var width = Math.Max(30, size.Width + 18);
        var height = 26;
        var cap = new Rectangle(x, bounds.Top + (bounds.Height - height) / 2, width, height);
        Theme.FillRounded(graphics, cap, 5, palette.Card);
        Theme.StrokeRounded(graphics, cap, 5, palette.InputBorder);
        using (var underline = new SolidBrush(palette.InputBorder))
        {
            graphics.FillRectangle(underline, cap.Left + 4, cap.Bottom - 1, cap.Width - 8, 1);
        }
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            cap,
            palette.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        return width;
    }

    private List<string> BindingParts()
    {
        var parts = ModifierNames((HotkeyModifiers)_binding.Modifiers);
        parts.Add(KeyName((Keys)_binding.VirtualKey));
        return parts;
    }

    private static List<string> LiveParts() => ModifierNames((HotkeyModifiers)CurrentModifiers());

    private static List<string> ModifierNames(HotkeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        return parts;
    }

    internal static string KeyName(Keys key) => key.ToString();

    private static bool IsAssignableKey(Keys key) =>
        key is >= Keys.A and <= Keys.Z or >= Keys.F1 and <= Keys.F11;

    private static uint CurrentModifiers()
    {
        var modifiers = 0u;
        var pressed = ModifierKeys;
        if (pressed.HasFlag(Keys.Control)) modifiers |= (uint)HotkeyModifiers.Control;
        if (pressed.HasFlag(Keys.Alt)) modifiers |= (uint)HotkeyModifiers.Alt;
        if (pressed.HasFlag(Keys.Shift)) modifiers |= (uint)HotkeyModifiers.Shift;
        if (GetKeyState(VkLeftWindows) < 0 || GetKeyState(VkRightWindows) < 0)
        {
            modifiers |= (uint)HotkeyModifiers.Win;
        }
        return modifiers;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}

internal enum StatusKind
{
    Idle,
    Working,
    Success,
    Error
}

/// <summary>Single-line status strip with a state dot and a spinner while busy.</summary>
internal sealed class StatusPill : Control
{
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 40 };
    private StatusKind _kind = StatusKind.Idle;
    private float _angle;

    public StatusPill()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Height = 44;
        _spinner.Tick += (_, _) =>
        {
            _angle = (_angle + 24F) % 360F;
            Invalidate();
        };
    }

    public StatusKind Kind
    {
        get => _kind;
        private set
        {
            _kind = value;
            if (_kind == StatusKind.Working)
            {
                _spinner.Start();
            }
            else
            {
                _spinner.Stop();
            }
            Invalidate();
        }
    }

    public void Show(string message, StatusKind kind)
    {
        Text = message;
        Kind = kind;
        AccessibleDescription = message;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Backdrop(this));
        var palette = Theme.Colors;
        var (fill, accent) = _kind switch
        {
            StatusKind.Error => (palette.ErrorFill, palette.Error),
            StatusKind.Working => (palette.WarningFill, palette.Warning),
            StatusKind.Success => (palette.SuccessFill, palette.Success),
            _ => (palette.Card, palette.Muted)
        };

        var bounds = Theme.Strokeable(ClientRectangle);
        Theme.FillRounded(graphics, bounds, 9, fill);
        Theme.StrokeRounded(graphics, bounds, 9, _kind == StatusKind.Idle ? palette.CardBorder : Color.FromArgb(60, accent));

        var glyph = new Rectangle(bounds.Left + 13, bounds.Top + (bounds.Height - 14) / 2, 14, 14);
        if (_kind == StatusKind.Working)
        {
            using var pen = new Pen(accent, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(pen, glyph, _angle, 250F);
        }
        else
        {
            using var brush = new SolidBrush(accent);
            graphics.FillEllipse(brush, glyph.Left + 3, glyph.Top + 3, 8, 8);
        }

        var textBounds = new Rectangle(bounds.Left + 36, bounds.Top, Math.Max(0, bounds.Width - 48), bounds.Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Theme.Text(9F),
            textBounds,
            _kind == StatusKind.Idle ? palette.Muted : accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Checklist in the side rail showing how far setup has progressed.</summary>
internal sealed class StepList : Control
{
    internal sealed record Step(string Title, bool Done);

    private const int RowHeight = 34;
    private IReadOnlyList<Step> _steps = [];

    public StepList()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    public void SetSteps(IReadOnlyList<Step> steps)
    {
        _steps = steps;
        AccessibleDescription = string.Join(", ", steps.Select(step => $"{step.Title}: {(step.Done ? "done" : "pending")}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = Theme.Colors;
        var accent = palette.Accent;
        var firstPending = -1;
        for (var index = 0; index < _steps.Count && firstPending < 0; index++)
        {
            if (!_steps[index].Done)
            {
                firstPending = index;
            }
        }

        for (var index = 0; index < _steps.Count; index++)
        {
            var step = _steps[index];
            var top = index * RowHeight;
            var marker = new Rectangle(1, top + (RowHeight - 16) / 2, 16, 16);
            var isCurrent = index == firstPending;

            if (index < _steps.Count - 1)
            {
                var nextMarkerTop = (index + 1) * RowHeight + (RowHeight - 16) / 2;
                using var connector = new Pen(palette.RailDivider, 1F);
                graphics.DrawLine(connector, marker.Left + 8, marker.Bottom + 2, marker.Left + 8, nextMarkerTop - 2);
            }

            if (step.Done)
            {
                using var fill = new SolidBrush(accent);
                graphics.FillEllipse(fill, marker);
                using var tick = new Pen(palette.AccentInk, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLines(tick,
                [
                    new PointF(marker.Left + 4.5F, marker.Top + 8F),
                    new PointF(marker.Left + 7F, marker.Top + 10.5F),
                    new PointF(marker.Left + 11.5F, marker.Top + 5.5F)
                ]);
            }
            else
            {
                using var ring = new Pen(isCurrent ? accent : palette.RailDivider, isCurrent ? 2F : 1.4F);
                graphics.DrawEllipse(ring, marker);
            }

            TextRenderer.DrawText(
                graphics,
                step.Title,
                Theme.Text(9F, isCurrent ? FontStyle.Bold : FontStyle.Regular),
                new Rectangle(marker.Right + 11, top, Math.Max(0, Width - marker.Right - 11), RowHeight),
                step.Done || isCurrent ? palette.RailInk : palette.RailMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Brand mark: a glowing bulb drawn to match the tray icon.</summary>
internal sealed class BulbMark : Control
{
    public BulbMark()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var accent = Theme.Colors.Accent;
        var center = new Point(38, 40);

        var halo = new Rectangle(center.X - 38, center.Y - 38, 76, 76);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(halo);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(90, accent),
                SurroundColors = [Color.FromArgb(0, accent)]
            };
            graphics.FillEllipse(glow, halo);
        }

        using (var ring = new Pen(Color.FromArgb(90, accent), 1.4F))
        {
            graphics.DrawEllipse(ring, center.X - 27, center.Y - 27, 54, 54);
        }

        using (var bulb = new SolidBrush(accent))
        {
            graphics.FillEllipse(bulb, center.X - 15, center.Y - 19, 30, 30);
        }

        using (var filament = new Pen(Color.FromArgb(120, Theme.Colors.AccentInk), 1.5F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawLines(filament,
            [
                new PointF(center.X - 5F, center.Y - 2F),
                new PointF(center.X - 1.5F, center.Y + 3F),
                new PointF(center.X + 1.5F, center.Y - 2F),
                new PointF(center.X + 5F, center.Y + 3F)
            ]);
        }

        using var socket = new SolidBrush(Color.FromArgb(210, 200, 209, 224));
        graphics.FillRectangle(socket, center.X - 9, center.Y + 10, 18, 7);
        graphics.FillRectangle(socket, center.X - 6, center.Y + 19, 12, 3);
    }
}
