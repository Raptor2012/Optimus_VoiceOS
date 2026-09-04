namespace Optimus.Core.Hotkeys;

using System;

public sealed class MockHotkeyService : IHotkeyService
{
    public bool IsHooked { get; private set; }
    public HotkeyBinding CurrentBinding { get; set; } = HotkeyBinding.Default;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;
    public event EventHandler<string>? RegistrationFailed;

    public void Register()
    {
        IsHooked = true;
    }

    public void Unregister()
    {
        IsHooked = false;
    }

    public void SimulatePress()
    {
        if (IsHooked)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SimulateRelease()
    {
        if (IsHooked)
        {
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SimulateRegistrationFailure(string message)
    {
        RegistrationFailed?.Invoke(this, message);
    }

    public void Dispose()
    {
        IsHooked = false;
    }
}
