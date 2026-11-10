namespace Tbox.Desktop.Platform;

public sealed record FolderNavigationResult(bool Success, string? Message = null);
public sealed record FolderDialogTarget(long Id, DesktopPlacement Bounds, DesktopPlacement WorkArea, double Scale);

public interface IFolderNavigation : IDisposable
{
    string CurrentDirectory { get; }
    bool HasPermission { get; }
    bool RequestPermission();
    void Start();
    void Stop();
    // The shortcut that triggered the jump; its keys are released before the dialog is driven.
    Task<FolderNavigationResult> JumpAsync(Tbox.Core.Shortcut shortcut, CancellationToken cancellationToken);
    Task<FolderDialogTarget?> FindDialogAsync(long? retainedTarget, nint activeBar, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetOpenFoldersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> SearchFoldersAsync(string query, CancellationToken cancellationToken);
    Task<bool> RestoreDialogAsync(long target, nint activeBar, CancellationToken cancellationToken);
    Task<FolderNavigationResult> JumpToAsync(long target, nint activeBar, string path, Tbox.Core.Shortcut? shortcut, CancellationToken cancellationToken);
}
