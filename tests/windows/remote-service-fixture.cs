using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

internal sealed class RemoteServiceFixture : ServiceBase
{
    private RemoteServiceFixture()
    {
        ServiceName = "TboxRemoteServiceFixture";
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        IntPtr buffer, int size, out int used);

    private static string ReadTcbPrivilege(WindowsIdentity identity)
    {
        const int tokenPrivileges = 3, insufficientBuffer = 122, privilegeEnabled = 2;
        long tcb;
        if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out tcb))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int size;
        GetTokenInformation(identity.Token, tokenPrivileges, IntPtr.Zero, 0, out size);
        if (Marshal.GetLastWin32Error() != insufficientBuffer || size < sizeof(int))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            int used;
            if (!GetTokenInformation(identity.Token, tokenPrivileges, buffer, size, out used))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // TOKEN_PRIVILEGES starts with a DWORD count, then 12-byte LUID_AND_ATTRIBUTES entries.
            int count = Marshal.ReadInt32(buffer);
            if (count < 0 || count > (used - sizeof(int)) / 12)
                throw new InvalidOperationException("Invalid privilege list for the fixture service.");
            for (int i = 0; i < count; i++)
            {
                int offset = sizeof(int) + 12 * i;
                if (Marshal.ReadInt64(buffer, offset) == tcb)
                    return (Marshal.ReadInt32(buffer, offset + 8) & privilegeEnabled) != 0 ? "enabled" : "disabled";
            }
            return "absent";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    protected override void OnStart(string[] arguments)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User == null || !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new InvalidOperationException("The test service must run under LocalSystem.");
            string state = Path.Combine(Environment.SystemDirectory,
                @"config\systemprofile\AppData\Local\TboxRemoteServiceFixtureDiagnostics");
            using (var process = Process.GetCurrentProcess())
                File.WriteAllText(Path.Combine(state, process.Id + ".txt"), ReadTcbPrivilege(identity));
        }
    }

    private static void Main(string[] arguments)
    {
        if (arguments.Length != 1 || arguments[0] != "--tbox-service")
            throw new InvalidOperationException("The package must start this executable in service mode.");
        using (var service = new RemoteServiceFixture())
            Run(service);
    }
}
