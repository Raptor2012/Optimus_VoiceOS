namespace Optimus.Core.Hotkeys;

using System;

public interface IHotkeyService : IDisposable
{
    bool IsHooked { get; }
    HotkeyBinding CurrentBinding { get; set; }

    void Register();
    void Unregister();

    event EventHandler? HotkeyPressed;
    event EventHandler? HotkeyReleased;
    event EventHandler<string>? RegistrationFailed;
}
