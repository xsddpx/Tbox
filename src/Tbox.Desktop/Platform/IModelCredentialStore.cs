namespace Tbox.Desktop.Platform;

// Keys saved before 1.1.76 live in the system credential store. They are read once, copied into the
// plaintext model configuration, then deleted. New keys are never written here.
public interface IModelCredentialStore
{
    Task<string?> ReadAsync(Guid credentialId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid credentialId, CancellationToken cancellationToken = default);
}

internal static class ModelCredentialStore
{
    public static IModelCredentialStore? Create() => OperatingSystem.IsMacOS() ? new MacModelCredentialStore()
        : OperatingSystem.IsWindows() ? new WindowsModelCredentialStore() : null;
}
