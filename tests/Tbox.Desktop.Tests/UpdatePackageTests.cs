using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tbox.Desktop.Updates;
using Xunit;

namespace Tbox.Desktop.Tests;

public sealed class UpdatePackageTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("Tbox.app/../outside")]
    [InlineData("/Tbox.app/absolute")]
    [InlineData("Tbox.app/C:/outside")]
    [InlineData("Tbox.app\\outside")]
    [InlineData("Another.app/file")]
    public void RejectsArchiveTraversalBeforeExtraction(string name)
    {
        using var archive = new Archive(); archive.Add(name, "bad");
        Assert.Throws<InvalidDataException>(() => UpdatePackagePolicy.ValidateMacArchive(archive.Finish()));
    }

    [Theory]
    [InlineData("../../../../outside")]
    [InlineData("/tmp/outside")]
    public void RejectsEscapingSymbolicLinks(string target)
    {
        using var archive = new Archive(); archive.App(); archive.Add("Tbox.app/Contents/MacOS/link", target, link: true);
        Assert.Throws<InvalidDataException>(() => UpdatePackagePolicy.ValidateMacArchive(archive.Finish()));
    }

    [Fact]
    public void AllowsInternalMediaLibraryLinks()
    {
        using var archive = new Archive(); archive.App(); archive.Add("Tbox.app/Contents/MacOS/lib.1.dylib", "library");
        archive.Add("Tbox.app/Contents/MacOS/lib.dylib", "lib.1.dylib", link: true);
        UpdatePackagePolicy.ValidateMacArchive(archive.Finish());
    }
    [Fact]
    public void RefusesEntriesBeneathLinks()
    {
        using var archive = new Archive(); archive.App(); archive.Add("Tbox.app/Contents/link", "MacOS", link: true);
        archive.Add("Tbox.app/Contents/link/injected", "bad");
        Assert.Throws<InvalidDataException>(() => UpdatePackagePolicy.ValidateMacArchive(archive.Finish()));
    }
    [Fact]
    public void RefusesLinkChainsAndCaseInsensitiveDuplicatePaths()
    {
        using var archive = new Archive(); archive.App();
        archive.Add("Tbox.app/Contents/MacOS/link1", "link2", link: true);
        archive.Add("Tbox.app/Contents/MacOS/link2", "Tbox.Desktop", link: true);
        Assert.Throws<InvalidDataException>(() => UpdatePackagePolicy.ValidateMacArchive(archive.Finish()));
        using var duplicate = new Archive(); duplicate.App(); duplicate.Add("Tbox.app/Contents/INFO.PLIST", "duplicate");
        Assert.Throws<InvalidDataException>(() => UpdatePackagePolicy.ValidateMacArchive(duplicate.Finish()));
    }
    [Fact]
    public async Task ChangedPackageIsRejectedAgainBeforeInstallation()
    {
        using var archive = new Archive(); archive.App(); var path = archive.Finish();
        var bytes = await File.ReadAllBytesAsync(path);
        var release = new UpdateRelease(new Version(1, 1, 7, 0), 1, "update.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "");
        await UpdatePackagePolicy.VerifyDigestAsync(path, release, CancellationToken.None);
        bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackagePolicy.VerifyDigestAsync(path, release, CancellationToken.None));
    }

    [Theory]
    [InlineData("Other", "CN=Tbox Development", "1.1.7.0", "x64", "App", "Tbox.Desktop.exe", false)]
    [InlineData("Tbox", "CN=Someone Else", "1.1.7.0", "x64", "App", "Tbox.Desktop.exe", false)]
    [InlineData("Tbox", "CN=Tbox Development", "1.1.6.0", "x64", "App", "Tbox.Desktop.exe", false)]
    [InlineData("Tbox", "CN=Tbox Development", "1.1.7.0", "arm64", "App", "Tbox.Desktop.exe", false)]
    [InlineData("Tbox", "CN=Tbox Development", "1.1.7.0", "x64", "Other", "Tbox.Desktop.exe", false)]
    [InlineData("Tbox", "CN=Tbox Development", "1.1.7.0", "x64", "App", "Other.exe", false)]
    [InlineData("Tbox", "CN=Tbox Development", "1.1.7.0", "x64", "App", "Tbox.Desktop.exe", true)]
    public void WindowsUpgradeRequiresExactIdentityVersionAndArchitecture(string name, string publisher, string version, string arch, string id, string exe, bool valid)
    {
        using var archive = new Archive();
        archive.Add("AppxManifest.xml", $"""
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="{name}" Publisher="{publisher}" Version="{version}" ProcessorArchitecture="{arch}"/>
              <Applications><Application Id="{id}" Executable="{exe}"/></Applications>
            </Package>
            """);
        var file = archive.Finish();
        void Validate() => UpdatePackagePolicy.ValidateWindowsManifest(file, new Version(1, 1, 7, 0), "Tbox", "CN=Tbox Development");
        if (valid) Validate(); else Assert.Throws<InvalidDataException>(Validate);
    }

    private sealed class Archive : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "tbox-package-test-" + Guid.NewGuid().ToString("N") + ".zip");
        private ZipArchive? _zip;
        public Archive() => _zip = ZipFile.Open(_path, ZipArchiveMode.Create);
        public void Add(string name, string value, bool link = false)
        {
            var entry = _zip!.CreateEntry(name); entry.ExternalAttributes = unchecked((link ? 0xa1ff : 0x81a4) << 16);
            using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false)); output.Write(value);
        }
        public void App() { Add("Tbox.app/Contents/Info.plist", "plist"); Add("Tbox.app/Contents/MacOS/Tbox.Desktop", "exe"); }
        public string Finish() { _zip?.Dispose(); _zip = null; return _path; }
        public void Dispose() { _zip?.Dispose(); File.Delete(_path); }
    }
}
