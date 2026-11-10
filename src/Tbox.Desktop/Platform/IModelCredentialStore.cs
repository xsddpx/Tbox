namespace Tbox.Desktop.Platform;

// Metadata checks must not retrieve the secret or prompt merely because settings were opened.
public interface IModelCredentialStore
{
    Task<bool> ContainsAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<string?> ReadAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task WriteAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid connectionId, CancellationToken cancellationToken = default);
}

internal static class ModelCredentialStore
{
    public static IModelCredentialStore Create() => OperatingSystem.IsMacOS() ? new MacModelCredentialStore()
        : OperatingSystem.IsWindows() ? new WindowsModelCredentialStore() : new Unavailable();

    private sealed class Unavailable : IModelCredentialStore
    {
        private static Exception Error() => new Tbox.Core.ModelServices.ModelServiceException(Tbox.Core.ModelServices.ModelFailure.Credentials, "当前平台不支持系统凭据存储。");
        public Task<bool> ContainsAsync(Guid id, CancellationToken ct) => Task.FromException<bool>(Error());
        public Task<string?> ReadAsync(Guid id, CancellationToken ct) => Task.FromException<string?>(Error());
        public Task WriteAsync(Guid id, string secret, CancellationToken ct) => Task.FromException(Error());
        public Task DeleteAsync(Guid id, CancellationToken ct) => Task.FromException(Error());
    }
}
