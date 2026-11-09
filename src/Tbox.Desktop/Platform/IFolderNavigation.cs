namespace Tbox.Desktop.Platform;

public sealed record FolderNavigationResult(bool Success, string? Message = null);

public interface IFolderNavigation : IDisposable
{
    string CurrentDirectory { get; }
    bool HasPermission { get; }
    bool RequestPermission();
    void Start();
    void Stop();
    // The shortcut that triggered the jump; its keys are released before the dialog is driven.
    Task<FolderNavigationResult> JumpAsync(Tbox.Core.Shortcut shortcut, CancellationToken cancellationToken);
}
