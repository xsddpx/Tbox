#if WINDOWS
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

// Activation the way PowerToys Peek does it. A low-level keyboard hook watches for Space (or the 速览 shortcut).
// Bare Space is never swallowed: Explorer still receives it. The shortcut is swallowed only when 速览 takes it.
// The foreground window is cached from EVENT_SYSTEM_FOREGROUND with a 40 ms debounce so the hook stays cheap.
internal sealed class QuickLookActivation : IDisposable
{
    private const int WhKeyboardLl = 13, WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, WmKeyUp = 0x0101, WmSysKeyUp = 0x0105;
    private readonly HookProc _hookProc;
    private readonly NativeMethods.WinEventDelegate _foregroundProc;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new();
    private uint _threadId;
    private nint _hook, _foregroundHook;
    private System.Threading.Timer? _debounce;
    private volatile bool _activatable;
    private volatile bool _spaceMode;
    private volatile Shortcut _shortcut = new(true, false, false, false, "Space");
    private volatile Action<nint>? _activated;
    private int _pressedKey;
    private readonly HashSet<int> _down = [];

    public bool Paused { get; set; }
    public nint PreviewWindow { get; set; }

    public QuickLookActivation()
    {
        _hookProc = OnKey; _foregroundProc = OnForeground;
        // The hook needs a thread that pumps messages; the UI thread may be busy decoding a preview.
        _thread = new Thread(Pump) { IsBackground = true, Name = "Tbox QuickLook keyboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.Wait();
    }

    public void Configure(bool spaceMode, Shortcut shortcut, Action<nint>? activated)
    {
        _spaceMode = spaceMode; _shortcut = shortcut; _activated = activated;
        UpdateActivatable();
    }

    private void Pump()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground, 0, _foregroundProc, 0, 0, NativeMethods.WineventOutofcontext);
        _debounce = new System.Threading.Timer(_ => UpdateActivatable());
        UpdateActivatable();
        _ready.TrySetResult();
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        if (_foregroundHook != 0) NativeMethods.UnhookWinEvent(_foregroundHook);
    }

    private void OnForeground(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time) =>
        _debounce?.Change(40, Timeout.Infinite);

    private void UpdateActivatable()
    {
        try { _activatable = QuickLookWindows.IsActivatable(NativeMethods.GetForegroundWindow(), PreviewWindow); }
        catch (Exception error) { AppLog.Write("QuickLook foreground: " + error.GetType().Name); _activatable = false; }
    }

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(0, code, wParam, lParam);
        var key = Marshal.ReadInt32(lParam);
        var message = (int)wParam;
        try
        {
            if (message is WmKeyUp or WmSysKeyUp)
            {
                _down.Remove(key);
                if (key == _pressedKey) { _pressedKey = 0; if (_swallowedUp) { _swallowedUp = false; return 1; } }
                return CallNextHookEx(0, code, wParam, lParam);
            }
            if (message is not (WmKeyDown or WmSysKeyDown)) return CallNextHookEx(0, code, wParam, lParam);
            // A held key repeats WM_KEYDOWN; only the first press counts, like RegisterHotKey with MOD_NOREPEAT.
            var repeat = !_down.Add(key);
            if (repeat || Paused || _activated is not { } activated) return CallNextHookEx(0, code, wParam, lParam);
            if (!Matches(key)) return CallNextHookEx(0, code, wParam, lParam);
            var foreground = NativeMethods.GetForegroundWindow();
            // The preview window always takes its own activation key so a second press closes it.
            var inPreview = foreground != 0 && foreground == PreviewWindow;
            if (!inPreview && (!_activatable || QuickLookWindows.IsEditing(foreground))) return CallNextHookEx(0, code, wParam, lParam);
            _pressedKey = key;
            activated(foreground);
            // A bare Space reaches Explorer as Peek's on_hotkey returns false; inside the preview it is ours.
            if (_spaceMode && !inPreview) return CallNextHookEx(0, code, wParam, lParam);
            _swallowedUp = true;
            return 1;
        }
        catch (Exception error)
        {
            AppLog.Write("QuickLook key hook: " + error.GetType().Name);
            return CallNextHookEx(0, code, wParam, lParam);
        }
    }
    private bool _swallowedUp;

    private bool Matches(int key)
    {
        static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        bool control = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);
        if (_spaceMode) return key == 0x20 && !control && !alt && !shift && !win;
        var shortcut = _shortcut;
        return ShortcutPolicy.WindowsKeyCode(shortcut.Key) == key && shortcut.Control == control && shortcut.Alt == alt &&
            shortcut.Shift == shift && shortcut.Meta == win;
    }

    public void Dispose()
    {
        _activated = null;
        _debounce?.Dispose();
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012, 0, 0);
        _thread.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; }
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
}
#endif
