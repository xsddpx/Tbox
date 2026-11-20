using System.Formats.Tar;
using System.IO.Compression;

namespace Tbox.Core;

public sealed class QuickLookArchiveEntry(string name, bool isDirectory)
{
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public long Size { get; set; }
    public List<QuickLookArchiveEntry> Children { get; } = [];
}

// The archive listing Peek shows: a tree with folders before files, each sorted by name, and three totals.
public sealed record QuickLookArchive(IReadOnlyList<QuickLookArchiveEntry> Root, int Directories, int Files, long ArchiveSize, long ExtractedSize)
{
    public const int MaximumEntries = 100_000;

    public static QuickLookArchive Read(string path, CancellationToken cancellationToken)
    {
        var archiveSize = new FileInfo(path).Length;
        var builder = new Builder();
        var extension = Path.GetExtension(path);
        if (extension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            ReadTar(stream, builder, cancellationToken);
        }
        else if (extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            ReadTar(stream, builder, cancellationToken);
        }
        else if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            // A single compressed file: one entry named after the archive, with its decompressed length.
            using var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0) { cancellationToken.ThrowIfCancellationRequested(); length += read; }
            builder.Add(Path.GetFileNameWithoutExtension(path), false, length);
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                builder.Add(entry.FullName, directory, directory ? 0 : entry.Length);
            }
        }
        return builder.Build(archiveSize);
    }

    private static void ReadTar(Stream stream, Builder builder, CancellationToken cancellationToken)
    {
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory) builder.Add(entry.Name, true, 0);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                builder.Add(entry.Name, false, entry.Length);
        }
    }

    private sealed class Builder
    {
        private readonly QuickLookArchiveEntry _root = new("", true);
        private readonly Dictionary<string, QuickLookArchiveEntry> _directories = new(StringComparer.OrdinalIgnoreCase);
        private int _count, _directoryCount, _fileCount;
        private long _extracted;

        public void Add(string fullName, bool isDirectory, long size)
        {
            if (++_count > MaximumEntries) throw new InvalidDataException("压缩包内的项目过多，无法预览。");
            var parts = fullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var parent = _root;
            var key = "";
            var folders = isDirectory ? parts.Length : parts.Length - 1;
            for (var i = 0; i < folders; i++)
            {
                key += parts[i] + "/";
                if (!_directories.TryGetValue(key, out var folder))
                {
                    folder = new QuickLookArchiveEntry(parts[i], true);
                    _directories[key] = folder; parent.Children.Add(folder); _directoryCount++;
                }
                parent = folder;
            }
            if (isDirectory) return;
            parent.Children.Add(new QuickLookArchiveEntry(parts[^1], false) { Size = size });
            _fileCount++; _extracted += size;
        }

        public QuickLookArchive Build(long archiveSize)
        {
            Sort(_root);
            return new QuickLookArchive(_root.Children, _directoryCount, _fileCount, archiveSize, _extracted);
        }

        private static void Sort(QuickLookArchiveEntry entry)
        {
            entry.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1)
                : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            foreach (var child in entry.Children) if (child.IsDirectory) Sort(child);
        }
    }
}

public readonly record struct QuickLookFolderTotals(long Bytes, long Files, long Folders, bool Incomplete, bool Done);

// Peek's folder size: recursive like the Explorer properties dialog, skips reparse points, ignores items it cannot read and marks the total incomplete.
public static class QuickLookFolderScan
{
    public static QuickLookFolderTotals Run(string path, Action<QuickLookFolderTotals> progress, TimeSpan interval, CancellationToken cancellationToken)
    {
        long bytes = 0, files = 0, folders = 0;
        var incomplete = false;
        var next = Environment.TickCount64 + (long)interval.TotalMilliseconds;
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (info is DirectoryInfo)
                    {
                        folders++;
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(info.FullName);
                    }
                    else
                    {
                        files++;
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) bytes += ((FileInfo)info).Length;
                    }
                    if (Environment.TickCount64 >= next)
                    {
                        progress(new(bytes, files, folders, incomplete, false));
                        next = Environment.TickCount64 + (long)interval.TotalMilliseconds;
                    }
                }
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { incomplete = true; }
        }
        var result = new QuickLookFolderTotals(bytes, files, folders, incomplete, true);
        progress(result);
        return result;
    }
}
