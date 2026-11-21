namespace Tbox.Desktop.Platform;

public sealed record TopmostWindow(long Id, string Title);

public interface IWindowTopmost : IDisposable
{
    IReadOnlyList<TopmostWindow> Windows { get; }
    bool NeedsScreenPermission => false;
    bool NeedsAccessibilityPermission => false;
    void RequestAccessibilityPermission() { }
    event Action<string?>? Changed;
    void ToggleForeground();
    void SetBorderVisible(bool visible);
    void Remove(long id);
    void Clear();
}
