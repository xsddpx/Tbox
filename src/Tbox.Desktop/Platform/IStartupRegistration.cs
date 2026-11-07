namespace Tbox.Desktop.Platform;

public sealed record StartupState(bool Enabled, bool RequiresApproval = false, string? Message = null);

public interface IStartupRegistration
{
    Task<StartupState> ReadAsync();
    Task<StartupState> SetEnabledAsync(bool enabled);
}
