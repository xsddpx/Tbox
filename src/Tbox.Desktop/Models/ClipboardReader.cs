using System.Runtime.InteropServices;

namespace Tbox.Desktop.Models;

public static class ClipboardReader
{
    public static async Task<T?> ReadAsync<T>(IReadOnlyList<Func<Task<T?>>> sources, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null) where T : class
    {
        delay ??= Task.Delay;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var busy = false;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (await source() is { } value) return value; }
                catch (Exception error) when (IsUnavailableFormat(error)) { busy |= IsBusy(error); }
            }
            if (!busy) return null;
            if (attempt < 2) await delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
        }
        throw new InvalidOperationException("剪贴板暂时被其他应用占用，请稍后重试。");
    }

    public static bool IsUnavailableFormat(Exception error) => error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException
        or InvalidOperationException or COMException;
    private static bool IsBusy(Exception error) => error.HResult == unchecked((int)0x800401D0) ||
        error.InnerException is { } inner && IsBusy(inner);
}
