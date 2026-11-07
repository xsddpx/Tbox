namespace Tbox.Desktop.Models;

// Ownership of each CTS remains with the caller until its async operation has completed.
public sealed class LatestOperation : IDisposable
{
    private CancellationTokenSource? _current;

    public CancellationTokenSource Begin()
    {
        _current?.Cancel();
        return _current = new CancellationTokenSource();
    }

    public bool IsCurrent(CancellationTokenSource operation) =>
        ReferenceEquals(_current, operation) && !operation.IsCancellationRequested;

    public void Complete(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_current, operation)) _current = null;
        operation.Dispose();
    }

    public void Dispose()
    {
        _current?.Cancel();
        _current = null;
    }
}
