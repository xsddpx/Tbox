namespace Tbox.Desktop.Platform;

public sealed record FolderNavigationResult(bool Success, string? Message = null);

public interface IFolderNavigation : IDisposable
{
    string CurrentDirectory { get; }
    bool HasPermission { get; }
    bool RequestPermission();
    void Start();
    void Stop();
    Task<FolderNavigationResult> JumpAsync(CancellationToken cancellationToken);
}
