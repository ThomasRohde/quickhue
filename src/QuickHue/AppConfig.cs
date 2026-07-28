using System.Text.Json.Serialization;

namespace QuickHue;

internal sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string BridgeId { get; set; } = string.Empty;
    public string BridgeAddress { get; set; } = string.Empty;
    public string CertificateSha256 { get; set; } = string.Empty;
    public string ProtectedApplicationKey { get; set; } = string.Empty;
    public LightTarget Target { get; set; } = new();
    public HotkeyBinding Hotkey { get; set; } = HotkeyBinding.Default;
    public bool StartWithWindows { get; set; } = true;

    [JsonIgnore]
    public bool IsConfigured =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(BridgeId) &&
        !string.IsNullOrWhiteSpace(BridgeAddress) &&
        CertificateSha256.Length == 64 &&
        CertificateSha256.All(Uri.IsHexDigit) &&
        !string.IsNullOrWhiteSpace(ProtectedApplicationKey) &&
        !string.IsNullOrWhiteSpace(Target.Id) &&
        Hotkey.IsValid();

    public AppConfig Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        BridgeId = BridgeId,
        BridgeAddress = BridgeAddress,
        CertificateSha256 = CertificateSha256,
        ProtectedApplicationKey = ProtectedApplicationKey,
        Target = new LightTarget { Id = Target.Id, Name = Target.Name },
        Hotkey = new HotkeyBinding { Modifiers = Hotkey.Modifiers, VirtualKey = Hotkey.VirtualKey },
        StartWithWindows = StartWithWindows
    };
}

internal sealed class LightTarget
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

[Flags]
internal enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}

internal sealed class HotkeyBinding
{
    public uint Modifiers { get; set; } = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt);
    public uint VirtualKey { get; set; } = (uint)Keys.L;

    [JsonIgnore]
    public static HotkeyBinding Default => new();

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            var modifiers = (HotkeyModifiers)Modifiers;
            if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(((Keys)VirtualKey).ToString());
            return string.Join("+", parts);
        }
    }

    public bool IsValid()
    {
        var validKey =
            VirtualKey is >= (uint)Keys.A and <= (uint)Keys.Z or
                >= (uint)Keys.F1 and <= (uint)Keys.F11;
        return validKey && (Modifiers & 0x000F) != 0;
    }
}
