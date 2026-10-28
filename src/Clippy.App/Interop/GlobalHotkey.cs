using System.Runtime.InteropServices;
using Clippy.Core.Hotkey;

namespace Clippy.App.Interop;

/// <summary>
/// Owns the global shortcut. Tries a native registration first; when another
/// process already owns the combination, suppresses it with a low-level
/// keyboard hook instead.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0xC11B;

    private readonly Action _onPressed;
    private readonly HotkeyWindow _window;
    private HotkeyBinding _binding;
    private LowLevelKeyboardHook? _hook;
    private bool _nativeRegistered;
    private bool _disposed;

    internal GlobalHotkey(Action onPressed, HotkeyBinding binding)
    {
        _onPressed = onPressed;
        _binding = binding;
        _window = new HotkeyWindow();
        _window.HotkeyPressed += () => _onPressed();
    }

    internal HotkeyBinding Binding => _binding;

    /// <summary>Installs the current hotkey and reports which strategy is active.</summary>
    internal HotkeyPlan Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _nativeRegistered = NativeMethods.RegisterHotKey(
            _window.Handle,
            HotkeyId,
            NativeModifiers(_binding.Modifiers),
            _binding.VirtualKey);

        var error = _nativeRegistered ? 0 : Marshal.GetLastWin32Error();
        var plan = HotkeyDecision.Decide(new HotkeyRegistration(_nativeRegistered, error));

        if (plan.Strategy == HotkeyStrategy.LowLevelHook)
        {
            _hook = new LowLevelKeyboardHook(_onPressed, _binding);
        }

        return plan;
    }

    /// <summary>
    /// Switches to a different combination without recreating the window.
    /// Returns the plan for the new binding.
    /// </summary>
    internal HotkeyPlan Rebind(HotkeyBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Release();

        _binding = binding;
        return Install();
    }

    /// <summary>
    /// Retries a native registration after Windows clipboard history has been
    /// switched off, releasing the combination. Returns the resulting plan.
    /// </summary>
    internal HotkeyPlan RetryNative()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_nativeRegistered)
        {
            return new HotkeyPlan(HotkeyStrategy.Native, false, 0);
        }

        _hook?.Dispose();
        _hook = null;

        return Install();
    }

    private void Release()
    {
        if (_nativeRegistered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _nativeRegistered = false;
        }

        _hook?.Dispose();
        _hook = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        _window.DestroyHandle();
    }

    /// <summary>Translates the app's modifier flags into <c>RegisterHotKey</c> flags.</summary>
    internal static uint NativeModifiers(uint modifiers)
    {
        var result = NativeMethods.MOD_NOREPEAT;

        if ((modifiers & HotkeyBinding.Alt) != 0)
        {
            result |= NativeMethods.MOD_ALT;
        }

        if ((modifiers & HotkeyBinding.Control) != 0)
        {
            result |= NativeMethods.MOD_CONTROL;
        }

        if ((modifiers & HotkeyBinding.Shift) != 0)
        {
            result |= NativeMethods.MOD_SHIFT;
        }

        if ((modifiers & HotkeyBinding.Win) != 0)
        {
            result |= NativeMethods.MOD_WIN;
        }

        return result;
    }

    /// <summary>Message-only window that receives <c>WM_HOTKEY</c>.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        internal event Action? HotkeyPressed;

        internal HotkeyWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "ClippyHotkey",
                Parent = NativeMethods.HWND_MESSAGE,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt64() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                return;
            }

            base.WndProc(ref m);
        }
    }
}

/// <summary>
/// Swallows the configured chord before any other application sees it. The
/// callback has to stay trivially fast: Windows silently unhooks a low-level
/// hook whose callback takes longer than the LowLevelHooksTimeout budget
/// (~300 ms).
/// </summary>
internal sealed class LowLevelKeyboardHook : IDisposable
{
    // Held in a field so the GC cannot collect the delegate while Windows holds it.
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly Action _onTriggered;
    private readonly HotkeyBinding _binding;
    private readonly IntPtr _moduleHandle;
    private IntPtr _hook;
    private bool _controlDown;
    private bool _altDown;
    private bool _shiftDown;
    private bool _winDown;
    private bool _targetDown;
    private bool _chordSwallowed;
    private bool _disposed;

    internal LowLevelKeyboardHook(Action onTriggered, HotkeyBinding binding)
    {
        _onTriggered = onTriggered;
        _binding = binding;
        _callback = Handle;
        _moduleHandle = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, _moduleHandle, 0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    internal bool IsInstalled => _hook != IntPtr.Zero;

    private IntPtr Handle(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Never intercept our own synthetic paste keystrokes.
        if ((info.flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (HotkeyBinding.IsModifierKey(info.vkCode))
        {
            if (isUp && _chordSwallowed && IsWindowsKey(info.vkCode))
            {
                // The shell saw Win go down but never saw the second half, so
                // its own Win-up logic opens the Start menu. Swallow that edge.
                _chordSwallowed = false;
                _winDown = false;
                return new IntPtr(1);
            }

            UpdateModifier(info.vkCode, isDown);

            if (isDown && IsWindowsKey(info.vkCode))
            {
                // A fresh Win press starts a new chord.
                _chordSwallowed = false;
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (info.vkCode != _binding.VirtualKey)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (isDown)
        {
            if (!_targetDown && CurrentModifiers() == _binding.Modifiers)
            {
                _targetDown = true;
                _chordSwallowed = true;
                _onTriggered();
                return new IntPtr(1);
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (isUp)
        {
            _targetDown = false;

            if (_chordSwallowed)
            {
                if (!_binding.UsesWindowsKey)
                {
                    _chordSwallowed = false;
                }

                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private uint CurrentModifiers()
    {
        var modifiers = 0u;

        if (_controlDown)
        {
            modifiers |= HotkeyBinding.Control;
        }

        if (_altDown)
        {
            modifiers |= HotkeyBinding.Alt;
        }

        if (_shiftDown)
        {
            modifiers |= HotkeyBinding.Shift;
        }

        if (_winDown)
        {
            modifiers |= HotkeyBinding.Win;
        }

        return modifiers;
    }

    private void UpdateModifier(int virtualKey, bool down)
    {
        switch (virtualKey)
        {
            case NativeMethods.VK_CONTROL:
            case NativeMethods.VK_LCONTROL:
            case NativeMethods.VK_RCONTROL:
                _controlDown = down;
                break;

            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LMENU:
            case NativeMethods.VK_RMENU:
                _altDown = down;
                break;

            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_LSHIFT:
            case NativeMethods.VK_RSHIFT:
                _shiftDown = down;
                break;

            case NativeMethods.VK_LWIN:
            case NativeMethods.VK_RWIN:
                _winDown = down;
                break;
        }
    }

    private static bool IsWindowsKey(int virtualKey) =>
        virtualKey is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
