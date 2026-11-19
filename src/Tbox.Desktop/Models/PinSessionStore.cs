using System.IO.Compression;
using System.Text.Json;
using Tbox.Core;
using Tbox.Desktop.Platform;

namespace Tbox.Desktop.Models;

internal sealed record SavedPin(DesktopPlacement Bounds, double PixelsPerUnit, double Zoom, PixelRegion Region,
    AnnotationSnapshotData Annotations, bool Visible, string? CachePath);
internal sealed record PinSessionImage(SavedPin State, byte[] Png);

// A single atomic file makes the editable state and its original images survive cache pruning together.
internal sealed class PinSessionStore(string directory)
{
    private sealed record Manifest(string Version, bool Hidden, SavedPin[] Pins);
    private readonly string _path = Path.Combine(directory, "update-pins.zip");

    public void Save(string version, bool hidden, IReadOnlyList<PinSessionImage> pins)
    {
        if (pins.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var i = 0; i < pins.Count; i++)
                    {
                        using var image = archive.CreateEntry($"{i}.png", CompressionLevel.NoCompression).Open();
                        image.Write(pins[i].Png);
                    }
                    using var metadata = archive.CreateEntry("session.json").Open();
                    JsonSerializer.Serialize(metadata, new Manifest(version, hidden, pins.Select(p => p.State).ToArray()));
                }
                file.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Consume()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    public void PreserveUnreadableSession()
    {
        if (File.Exists(_path)) File.Move(_path, Path.Combine(directory, $"update-pins-recovery-{Guid.NewGuid():N}.zip"));
    }

    public (bool Hidden, List<PinSessionImage> Pins) ReadForUpgrade(string version)
    {
        if (!File.Exists(_path)) return (false, []);
        using var archive = ZipFile.OpenRead(_path);
        using var metadata = Entry("session.json").Open();
        var session = JsonSerializer.Deserialize<Manifest>(metadata) ?? throw new InvalidDataException("贴图会话无效。");
        var pins = new List<PinSessionImage>();
        if (string.IsNullOrEmpty(session.Version) || session.Pins is null)
            throw new InvalidDataException("贴图会话缺少版本或贴图列表。");
        if (session.Version == version) return (false, pins);
        for (var i = 0; i < session.Pins.Length; i++)
        {
            var entry = Entry($"{i}.png");
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
            using var image = entry.Open();
            image.ReadExactly(bytes);
            pins.Add(new(session.Pins[i], bytes));
        }
        return (session.Hidden, pins);

        ZipArchiveEntry Entry(string name) => archive.GetEntry(name) ?? throw new InvalidDataException("贴图会话缺少文件。");
    }
}
