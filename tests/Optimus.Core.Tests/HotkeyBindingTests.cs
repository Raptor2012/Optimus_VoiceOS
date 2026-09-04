namespace Optimus.Core.Tests;

using Optimus.Core.Hotkeys;
using Xunit;

public class HotkeyBindingTests
{
    [Fact]
    public void DefaultBinding_IsF8WithNoModifiers()
    {
        var binding = HotkeyBinding.Default;
        Assert.Equal(0x77, binding.VirtualKeyCode);
        Assert.Equal(HotkeyModifiers.None, binding.Modifiers);
        Assert.Equal("F8", binding.DisplayName);
    }

    [Fact]
    public void CustomModifiers_FormatProperly()
    {
        var binding = new HotkeyBinding(0x20 /* Space */, HotkeyModifiers.Control | HotkeyModifiers.Shift);
        Assert.Equal("Ctrl+Shift+Space", binding.DisplayName);
    }

    [Fact]
    public void Equality_ComparesKeyAndModifiers()
    {
        var b1 = new HotkeyBinding(0x77, HotkeyModifiers.None);
        var b2 = new HotkeyBinding(0x77, HotkeyModifiers.None);
        var b3 = new HotkeyBinding(0x77, HotkeyModifiers.Control);

        Assert.Equal(b1, b2);
        Assert.NotEqual(b1, b3);
        Assert.Equal(b1.GetHashCode(), b2.GetHashCode());
    }
}
