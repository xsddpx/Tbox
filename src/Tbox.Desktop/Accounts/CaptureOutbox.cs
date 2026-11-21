using Tbox.Core.Accounts;

namespace Tbox.Desktop.Accounts;

internal sealed class CaptureOutbox(string root)
{
    private string DirectoryFor(LocalAccount account) => Path.Combine(root, account.DeviceId.ToString("N"));

    internal async Task EnqueueAsync(LocalAccount account, byte[] png, CancellationToken ct)
    {
        if (png.Length > CloudProtocol.MaximumCaptureBytes) throw new IOException("这张截图超过同步大小上限。");
        var directory = DirectoryFor(account);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, png, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path);
            Pending(account);
        }
        finally { File.Delete(temp); }
    }

    internal FileInfo[] Pending(LocalAccount account)
    {
        var directory = new DirectoryInfo(DirectoryFor(account));
        if (!directory.Exists) return [];
        var cutoff = DateTime.UtcNow.AddDays(-CloudProtocol.HistoryDays);
        var kept = new List<FileInfo>();
        long bytes = 0;
        foreach (var file in directory.EnumerateFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _) ||
                file.LastWriteTimeUtc <= cutoff || kept.Count >= CloudProtocol.MaximumCaptureCount ||
                file.Length > CloudProtocol.MaximumCaptureBytes || bytes + file.Length > CloudProtocol.MaximumHistoryBytes)
            {
                file.Delete();
                continue;
            }
            kept.Add(file);
            bytes += file.Length;
        }
        return kept.AsEnumerable().Reverse().ToArray();
    }

    internal void Clear(LocalAccount account)
    {
        var directory = DirectoryFor(account);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
