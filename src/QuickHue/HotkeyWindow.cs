using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QuickHue;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x5148;
    private const int WmHotkey = 0x0312;
    private bool _registered;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "QuickHueHotkeyWindow",
            Parent = new IntPtr(-3)
        });
    }

    public event EventHandler? Pressed;

    public bool TryRegister(HotkeyBinding binding, out string? error)
    {
        Unregister();
        if (!binding.IsValid())
        {
            error = "Choose at least one modifier and a letter or F1–F11 key.";
            return false;
        }

        var modifiers = binding.Modifiers | (uint)HotkeyModifiers.NoRepeat;
        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, binding.VirtualKey);
        error = _registered
            ? null
            : new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
