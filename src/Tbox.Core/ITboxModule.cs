namespace Tbox.Core;

public interface ITboxModule : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsEnabled { get; }
    event EventHandler<bool>? EnabledChanged;
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

