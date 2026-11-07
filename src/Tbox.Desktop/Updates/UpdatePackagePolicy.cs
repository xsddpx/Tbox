using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tbox.Desktop.Updates;

internal static class UpdatePackagePolicy
{
    public static async Task VerifyDigestAsync(string file, UpdateRelease release, CancellationToken token)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != release.Size || !CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(input, token), Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("更新包已发生变化，请重新下载。");
    }

    // Validate before invoking ditto, including paths encoded in Unix symlink entries.
    public static void ValidateMacArchive(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        if (zip.Entries.Count is 0 or > 100000) throw new InvalidDataException("更新包文件数量无效。");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tbox-archive-root"));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            var parts = name.Split('/');
            if (parts.Any(p => p is "" or "." or "..") || name.Contains('\\') || name.Contains(':') || name.Contains('\0') ||
                parts[0] is not ("Tbox.app" or "__MACOSX") || !paths.Add(name))
                throw new InvalidDataException("更新包包含不安全或重复的文件路径。");
            expanded = checked(expanded + entry.Length);
            if (expanded > 6L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包解压大小超过限制。");
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is not (0 or 0x8000 or 0x4000 or 0xa000)) throw new InvalidDataException("更新包含特殊文件。");
            if (kind != 0xa000) continue;
            if (entry.Length is <= 0 or > 4096 || parts[0] != "Tbox.app") throw new InvalidDataException("无效的更新文件链接。");
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true));
            var target = reader.ReadToEnd();
            if (Path.IsPathRooted(target) || target.Contains('\\') || target.Contains(':') || target.Contains('\0'))
                throw new InvalidDataException("更新文件链接指向应用外部。");
            var resolved = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(name)!, target));
            if (!resolved.StartsWith(Path.Combine(root, "Tbox.app") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("更新文件链接指向应用外部。");
            links.Add(name, Path.GetRelativePath(root, resolved).Replace(Path.DirectorySeparatorChar, '/'));
        }
        foreach (var link in links)
        {
            if (paths.Any(p => p.StartsWith(link.Key + "/", StringComparison.OrdinalIgnoreCase)) ||
                links.Keys.Any(p => link.Value.Equals(p, StringComparison.OrdinalIgnoreCase) || link.Value.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("更新包含嵌套或循环文件链接。");
        }
        if (!paths.Contains("Tbox.app/Contents/Info.plist") || !paths.Contains("Tbox.app/Contents/MacOS/Tbox.Desktop"))
            throw new InvalidDataException("更新包缺少应用文件。");
    }

    public static void ValidateWindowsManifest(string file, Version expected, string installedName, string installedPublisher)
    {
        using var zip = ZipFile.OpenRead(file);
        var entries = zip.Entries.Where(e => e.FullName == "AppxManifest.xml").ToArray();
        if (entries.Length != 1 || entries[0].Length > 1024 * 1024) throw new InvalidDataException("安装包清单无效。");
        using var stream = entries[0].Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var xml = XDocument.Load(reader); XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = xml.Root?.Element(ns + "Identity");
        var app = xml.Root?.Element(ns + "Applications")?.Elements(ns + "Application").SingleOrDefault();
        if ((string?)identity?.Attribute("Name") != installedName || (string?)identity?.Attribute("Publisher") != installedPublisher ||
            (string?)identity?.Attribute("ProcessorArchitecture") != "x64" || (string?)identity?.Attribute("Version") != expected.ToString(4) ||
            (string?)app?.Attribute("Id") != "App" || (string?)app?.Attribute("Executable") != "Tbox.Desktop.exe")
            throw new InvalidDataException("安装包身份、平台或版本与已安装应用不匹配。");
    }
}
