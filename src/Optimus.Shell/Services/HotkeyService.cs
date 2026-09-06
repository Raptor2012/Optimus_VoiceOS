namespace Optimus.Shell.Services;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Optimus.Core.Hotkeys;
using Optimus.Shell;

public enum CompanionHotkeyAction { ToggleListening, ToggleCompanion }

/// <summary>Small RegisterHotKey wrapper for companion-level shortcuts.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int IdListening = 0x4F01;
    private const int IdCompanion = 0x4F02;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint ModShift = 0x0004;
    private readonly PersonalSettings _preferences;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;
    private bool _listeningRegistered;
    private bool _companionRegistered;

    public HotkeyBinding ListeningBinding { get; private set; }
    public HotkeyBinding CompanionBinding { get; private set; }
    public event EventHandler<CompanionHotkeyAction>? Pressed;
    public event EventHandler<string>? RegistrationFailed;

    public HotkeyService(PersonalSettings? preferences = null)
    {
        _preferences = preferences ?? new PersonalSettings();
        ListeningBinding = Parse(_preferences.ToggleListeningHotkey, new HotkeyBinding(0x20, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+Space"));
        CompanionBinding = Parse(_preferences.ToggleCompanionHotkey, new HotkeyBinding(0x4F, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+O"));
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_disposed) return;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowHook);
        Register();
    }

    public void Register()
    {
        if (_handle == IntPtr.Zero || _disposed) return;
        _listeningRegistered = Register(_handle, IdListening, ListeningBinding);
        _companionRegistered = Register(_handle, IdCompanion, CompanionBinding);
        if (!_listeningRegistered || !_companionRegistered)
        {
            RegistrationFailed?.Invoke(this, "A companion shortcut is already in use by another application.");
        }
    }

    public void SetBinding(CompanionHotkeyAction action, HotkeyBinding binding)
    {
        if (action == CompanionHotkeyAction.ToggleListening) ListeningBinding = binding;
        else CompanionBinding = binding;
        Unregister();
        Register();
    }

    private static bool Register(IntPtr handle, int id, HotkeyBinding binding)
    {
        uint modifiers = ModNoRepeat;
        if ((binding.Modifiers & HotkeyModifiers.Control) != 0) modifiers |= ModControl;
        if ((binding.Modifiers & HotkeyModifiers.Shift) != 0) modifiers |= ModShift;
        if ((binding.Modifiers & HotkeyModifiers.Alt) != 0) modifiers |= ModAlt;
        if ((binding.Modifiers & HotkeyModifiers.Windows) != 0) modifiers |= 0x0008;
        return RegisterHotKey(handle, id, modifiers, (uint)binding.VirtualKeyCode);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (id == IdListening) Pressed?.Invoke(this, CompanionHotkeyAction.ToggleListening);
            else if (id == IdCompanion) Pressed?.Invoke(this, CompanionHotkeyAction.ToggleCompanion);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_handle == IntPtr.Zero) return;
        if (_listeningRegistered) UnregisterHotKey(_handle, IdListening);
        if (_companionRegistered) UnregisterHotKey(_handle, IdCompanion);
        _listeningRegistered = false;
        _companionRegistered = false;
    }

    private static HotkeyBinding Parse(string? value, HotkeyBinding fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return fallback;
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        int key = 0;
        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyModifiers.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyModifiers.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyModifiers.Alt;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= HotkeyModifiers.Windows;
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase)) key = 0x20;
            else if (part.Length == 1) key = char.ToUpperInvariant(part[0]);
            else if (part.StartsWith('F') && int.TryParse(part[1..], out int function) && function is >= 1 and <= 12) key = 0x6F + function;
        }
        return key == 0 ? fallback : HotkeyBinding.FromVirtualKey(key, modifiers);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        if (_source != null) _source.RemoveHook(WindowHook);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
