namespace Optimus.Core.Hotkeys;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed class WindowsKeyboardHook : IHotkeyService
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _isKeyDown;
    private bool _disposed;

    public bool IsHooked => _hookId != IntPtr.Zero;
    public HotkeyBinding CurrentBinding { get; set; } = HotkeyBinding.Default;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;
    public event EventHandler<string>? RegistrationFailed;

    public WindowsKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Register()
    {
        if (_disposed || IsHooked)
        {
            return;
        }

        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            IntPtr moduleHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);
            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                RegistrationFailed?.Invoke(this, $"Failed to register low-level keyboard hook (Win32 error: {error}).");
            }
        }
        catch (Exception ex)
        {
            RegistrationFailed?.Invoke(this, $"Failed to register keyboard hook: {ex.Message}");
        }
    }

    public void Unregister()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _isKeyDown = false;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)kb.vkCode;

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                if (vkCode == CurrentBinding.VirtualKeyCode && CheckModifiers(CurrentBinding.Modifiers))
                {
                    if (!_isKeyDown)
                    {
                        _isKeyDown = true;
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (vkCode == CurrentBinding.VirtualKeyCode)
                {
                    if (_isKeyDown)
                    {
                        _isKeyDown = false;
                        HotkeyReleased?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool CheckModifiers(HotkeyModifiers expected)
    {
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

        bool expectedCtrl = (expected & HotkeyModifiers.Control) != 0;
        bool expectedAlt = (expected & HotkeyModifiers.Alt) != 0;
        bool expectedShift = (expected & HotkeyModifiers.Shift) != 0;
        bool expectedWin = (expected & HotkeyModifiers.Windows) != 0;

        return ctrl == expectedCtrl && alt == expectedAlt && shift == expectedShift && win == expectedWin;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Unregister();
        }
    }
}
