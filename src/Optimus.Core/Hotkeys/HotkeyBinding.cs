namespace Optimus.Core.Hotkeys;

using System;
using System.Text;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed class HotkeyBinding : IEquatable<HotkeyBinding>
{
    public int VirtualKeyCode { get; }
    public HotkeyModifiers Modifiers { get; }
    public string DisplayName { get; }

    public HotkeyBinding(int virtualKeyCode, HotkeyModifiers modifiers = HotkeyModifiers.None, string? displayName = null)
    {
        VirtualKeyCode = virtualKeyCode;
        Modifiers = modifiers;
        DisplayName = displayName ?? FormatDisplayName(virtualKeyCode, modifiers);
    }

    public static HotkeyBinding Default => new(0x77 /* VK_F8 */, HotkeyModifiers.None, "F8");

    public static HotkeyBinding RightControl => new(0xA3 /* VK_RCONTROL */, HotkeyModifiers.None, "Right Ctrl");

    public static HotkeyBinding CapsLock => new(0x14 /* VK_CAPITAL */, HotkeyModifiers.None, "Caps Lock");

    public static HotkeyBinding FromVirtualKey(int vk, HotkeyModifiers modifiers = HotkeyModifiers.None)
    {
        return new HotkeyBinding(vk, modifiers);
    }

    private static string FormatDisplayName(int vk, HotkeyModifiers modifiers)
    {
        var sb = new StringBuilder();
        if ((modifiers & HotkeyModifiers.Control) != 0)
        {
            sb.Append("Ctrl+");
        }
        if ((modifiers & HotkeyModifiers.Alt) != 0)
        {
            sb.Append("Alt+");
        }
        if ((modifiers & HotkeyModifiers.Shift) != 0)
        {
            sb.Append("Shift+");
        }
        if ((modifiers & HotkeyModifiers.Windows) != 0)
        {
            sb.Append("Win+");
        }

        string keyName = vk switch
        {
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            0x14 => "Caps Lock",
            0x20 => "Space",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0x13 => "Pause",
            0x91 => "Scroll Lock",
            _ => $"Key(0x{vk:X2})"
        };

        sb.Append(keyName);
        return sb.ToString();
    }

    public bool Equals(HotkeyBinding? other)
    {
        if (other is null)
        {
            return false;
        }

        return VirtualKeyCode == other.VirtualKeyCode && Modifiers == other.Modifiers;
    }

    public override bool Equals(object? obj) => Equals(obj as HotkeyBinding);

    public override int GetHashCode() => HashCode.Combine(VirtualKeyCode, (int)Modifiers);

    public override string ToString() => DisplayName;
}
