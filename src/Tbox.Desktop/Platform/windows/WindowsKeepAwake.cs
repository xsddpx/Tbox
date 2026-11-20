#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tbox.Core;

namespace Tbox.Desktop.Platform;

// Power request objects are not tied to a thread (unlike SetThreadExecutionState) and are listed by `powercfg /requests`.
// The system drops them when the process ends; only the lid action needs LidCloseGuard's saved backup.
internal sealed class WindowsKeepAwake : IKeepAwake
{
    private const int PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1;
    private readonly LidCloseGuard _lid;
    private readonly object _sync = new();
    private nint _request;
    private KeepAwakeRequest _applied = KeepAwakeRequest.None;

    public WindowsKeepAwake(string stateDirectory) => _lid = new LidCloseGuard(new WindowsLidPowerPlan(), Path.Combine(stateDirectory, "keep-awake-lid.json"));

    public bool HasLid { get; } = DetectLid();

    public Task<KeepAwakeResult> ApplyAsync(KeepAwakeRequest request) => Task.Run(() =>
    {
        lock (_sync)
        {
            try
            {
                var display = request.System && request.Display;
                if (request.System) Set(PowerRequestSystemRequired); else Clear(PowerRequestSystemRequired, _applied.System);
                _applied = _applied with { System = request.System };
                if (display) Set(PowerRequestDisplayRequired); else Clear(PowerRequestDisplayRequired, _applied.Display);
                _applied = _applied with { Display = display };
            }
            catch (Win32Exception error)
            {
                AppLog.Write("Keep awake request failed: " + error);
                ReleaseLocked();
                return new KeepAwakeResult(_applied, $"系统拒绝保持唤醒：{error.Message}");
            }
            try
            {
                // Hold even when already held: the user may have switched power plans since.
                if (request.System && request.Lid && HasLid) _lid.Hold(); else _lid.Release();
                _applied = _applied with { Lid = request.System && request.Lid && HasLid };
            }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Lid action change failed: " + error);
                _applied = _applied with { Lid = _lid.Held };
                return new KeepAwakeResult(_applied, $"无法更改合上盖子时的操作：{error.Message}");
            }
            return new KeepAwakeResult(_applied);
        }
    });

    // Setting an already-set request type is harmless, so Set is idempotent.
    private void Set(int type)
    {
        if (_request == 0)
        {
            var reason = Marshal.StringToHGlobalUni("Tbox 保持唤醒");
            try
            {
                // POWER_REQUEST_CONTEXT_SIMPLE_STRING; the system copies the string.
                var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
                _request = PowerCreateRequest(ref context);
            }
            finally { Marshal.FreeHGlobal(reason); }
            if (_request == -1 || _request == 0) { _request = 0; throw new Win32Exception(Marshal.GetLastWin32Error()); }
        }
        if (!PowerSetRequest(_request, type)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void Clear(int type, bool set)
    {
        if (set && _request != 0) PowerClearRequest(_request, type);
    }

    private void ReleaseLocked()
    {
        if (_request != 0)
        {
            if (_applied.Display) PowerClearRequest(_request, PowerRequestDisplayRequired);
            if (_applied.System) PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request); _request = 0;
        }
        _applied = _applied with { System = false, Display = false };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ReleaseLocked();
            try { _lid.Release(); }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // The saved backup stays on disk and is written back on the next start.
                AppLog.Write("Lid action restore failed on exit: " + error);
            }
            _applied = KeepAwakeRequest.None;
        }
    }

    // SYSTEM_POWER_CAPABILITIES starts with three BOOLEANs: PowerButtonPresent, SleepButtonPresent, LidPresent.
    private static bool DetectLid()
    {
        var buffer = new byte[128];
        return CallNtPowerInformation(4, 0, 0, buffer, buffer.Length) == 0 && buffer[2] != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version, Flags;
        public nint SimpleReasonString;
        // Room for the detailed-string variant of the union, which the system may read the size of.
        public nint Reserved1, Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(nint request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(nint request, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, nint input, int inputLength, byte[] output, int outputLength);
}

internal sealed class WindowsLidPowerPlan : ILidPowerPlan
{
    private static readonly Guid Buttons = new("4f971e89-eebd-4455-a8de-9e59040e7347"), LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    public Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(0, out var pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public (uint PluggedIn, uint OnBattery) ReadLidAction(Guid scheme)
    {
        var buttons = Buttons; var lid = LidAction;
        Check(PowerReadACValueIndex(0, ref scheme, ref buttons, ref lid, out var pluggedIn));
        Check(PowerReadDCValueIndex(0, ref scheme, ref buttons, ref lid, out var onBattery));
        return (pluggedIn, onBattery);
    }

    public void WriteLidAction(Guid scheme, uint pluggedIn, uint onBattery)
    {
        var buttons = Buttons; var lid = LidAction;
        Check(PowerWriteACValueIndex(0, ref scheme, ref buttons, ref lid, pluggedIn));
        Check(PowerWriteDCValueIndex(0, ref scheme, ref buttons, ref lid, onBattery));
        // Changed values take effect only once the plan is applied again.
        if (ActiveScheme() == scheme) Check(PowerSetActiveScheme(0, ref scheme));
    }

    private static void Check(uint error) { if (error != 0) throw new Win32Exception((int)error); }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
#endif
