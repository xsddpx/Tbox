using System;
using System.Security.Principal;
using System.ServiceProcess;

internal sealed class RemoteServiceFixture : ServiceBase
{
    private RemoteServiceFixture()
    {
        ServiceName = "TboxRemoteServiceFixture";
    }

    protected override void OnStart(string[] arguments)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User == null || !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new InvalidOperationException("The test service must run under LocalSystem.");
        }
    }

    private static void Main()
    {
        using (var service = new RemoteServiceFixture())
            Run(service);
    }
}
