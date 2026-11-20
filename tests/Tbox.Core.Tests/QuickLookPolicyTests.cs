using System.IO.Compression;
using System.Text.Json;
using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class QuickLookPolicyTests
{
    [Theory]
    [InlineData("a.PNG", false, QuickLookKind.Image)]
    [InlineData("a.gif", false, QuickLookKind.Image)]
    [InlineData("a.mkv", false, QuickLookKind.Video)]
    [InlineData("a.pdf", false, QuickLookKind.Pdf)]
    [InlineData("a.zip", false, QuickLookKind.Archive)]
    [InlineData("a.tar.gz", false, QuickLookKind.Archive)]
    [InlineData("a.docx", false, QuickLookKind.Unsupported)]
    [InlineData("folder.zip", true, QuickLookKind.Folder)]
    public void ClassifiesByExtension(string path, bool directory, QuickLookKind expected) =>
        Assert.Equal(expected, QuickLookPolicy.Classify(path, directory));

    [Fact]
    public void SystemCodecsAddImageTypes()
    {
        Assert.Equal(QuickLookKind.Unsupported, QuickLookPolicy.Classify("a.heic", false));
        Assert.Equal(QuickLookKind.Image, QuickLookPolicy.Classify("a.heic", false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".HEIC" }));
    }

    [Fact]
    public void FitScalesDownOnlyThenClampsEachSide()
    {
        Assert.Equal((1000, 500), QuickLookPolicy.Fit(1600, 800, 1000, 1000, 500, 300));
        Assert.Equal((600, 300), QuickLookPolicy.Fit(600, 300, 1000, 1000, 500, 300));
        // The minimum is applied per side, so a thin image does not keep its ratio.
        Assert.Equal((500, 452), QuickLookPolicy.Fit(100, 20, 1000, 1000, 500, 452));
    }

    [Fact]
    public void WindowSizeFollowsPeek()
    {
        // A 4000×3000 photo on a 1920×1080 display at 100%: limited to 80% of the monitor below the title bar.
        var (width, height) = QuickLookPolicy.WindowSize((4000, 3000), false, 1920, 1080, 1);
        Assert.Equal(825.6, height - QuickLookPolicy.TitleBarHeight, 1);
        Assert.Equal(825.6 * 4 / 3, width, 1);
        // At 150% the same pixels are a third smaller in effective pixels.
        var (scaledWidth, _) = QuickLookPolicy.WindowSize((900, 600), false, 2880, 1620, 1.5);
        Assert.Equal(600, scaledWidth, 1);
        // The information page asks for 680×500 epx; the title bar adds 48.
        Assert.Equal((680d, 548d), QuickLookPolicy.WindowSize((680, 500), true, 1920, 1080, 1));
        // A preview without a size takes the monitor's shape within 80% of it.
        Assert.Equal(825.6 + QuickLookPolicy.TitleBarHeight, QuickLookPolicy.WindowSize(null, true, 1920, 1080, 1).Height, 1);
    }

    [Theory]
    [InlineData(0, "0 字节")]
    [InlineData(1023, "1023 字节")]
    [InlineData(1024, "1 KB (1,024 字节)")]
    [InlineData(1234567, "1.17 MB (1,234,567 字节)")]
    [InlineData(1048575, "1023 KB (1,048,575 字节)")]
    public void ReadableSizeTruncatesToThreeDigits(long bytes, string expected) =>
        Assert.Equal(expected, QuickLookPolicy.ReadableSize(bytes));

    [Fact]
    public void TitlesAndCounters()
    {
        Assert.Equal("速览", QuickLookPolicy.WindowTitle(null));
        Assert.Equal("a.png - 速览", QuickLookPolicy.WindowTitle("a.png"));
        Assert.Equal("", QuickLookPolicy.Counter(0, 1));
        Assert.Equal("(2/3)", QuickLookPolicy.Counter(1, 3));
        Assert.Equal("用 照片 打开", QuickLookPolicy.OpenWith("照片"));
        Assert.Equal("1 个文件, 2 个文件夹", QuickLookPolicy.FolderContents(1, 2));
        Assert.Equal("0 个文件", QuickLookPolicy.FolderContents(0, 0));
    }

    [Fact]
    public void NavigatorWrapsAndDeletesInTheLastDirection()
    {
        var items = new QuickLookNavigator(["a", "b", "c"]);
        Assert.Equal("c", items.Move(-1));
        Assert.Equal("a", items.Move(1));
        items.Move(1);
        Assert.Equal("c", items.RemoveCurrent());
        Assert.Equal((1, 2), (items.Index, items.Count));
        items.Move(-1);
        Assert.Equal("c", items.RemoveCurrent());
        Assert.Null(items.RemoveCurrent());
        Assert.Null(items.Current);
    }

    [Fact]
    public void NavigatorKeepsTheCurrentItemWhenTheFolderLoads()
    {
        var items = new QuickLookNavigator(["b"]);
        items.Replace(["a", "B", "c"], "b");
        Assert.Equal((1, "B"), (items.Index, items.Current));
        items.Replace(["x"], "gone");
        Assert.Equal("gone", items.Current);
    }

    [Fact]
    public void QuickLookShortcutDefaultsToCtrlSpaceButIsNotRegistered()
    {
        Assert.Equal("Ctrl+Space", ShortcutPolicy.Default(HotkeyAction.QuickLook, mac: false).Display(false));
        Assert.Null(ShortcutPolicy.Validate(ShortcutPolicy.Default(HotkeyAction.QuickLook, false), false));
        Assert.NotNull(ShortcutPolicy.Validate(new Shortcut(false, false, false, false, "Space"), false));
        Assert.DoesNotContain(HotkeyAction.QuickLook, ShortcutPolicy.Actions);
    }

    [Fact]
    public void SettingsDefaultToPeekAndRoundTrip()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!.QuickLook;
        Assert.True(defaults is { Enabled: true, SpaceToActivate: true, ShowInTaskbar: true, ConfirmFileDelete: true, ShowFileInfoTooltip: true,
            AlwaysOnTop: false, CloseAfterLosingFocus: false });
        var settings = new AppSettings { QuickLook = defaults with { SpaceToActivate = false, AlwaysOnTop = true } };
        Assert.Equal(settings, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)));
    }

    [Fact]
    public void ArchiveListsFoldersFirstWithTotals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbox-quicklook-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
                Add("z.txt", "zz"); Add("docs/b.txt", "bbb"); Add("docs/A.txt", "a"); zip.CreateEntry("empty/");
            }
            var archive = QuickLookArchive.Read(path, CancellationToken.None);
            Assert.Equal(["docs", "empty", "z.txt"], archive.Root.Select(entry => entry.Name));
            Assert.Equal(["A.txt", "b.txt"], archive.Root[0].Children.Select(entry => entry.Name));
            Assert.Equal((2, 3, 6L), (archive.Directories, archive.Files, archive.ExtractedSize));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FolderScanCountsRecursively()
    {
        var root = Directory.CreateTempSubdirectory("tbox-quicklook-");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "a"), new byte[10]);
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "child"));
            File.WriteAllBytes(Path.Combine(child.FullName, "b"), new byte[5]);
            var updates = 0;
            var totals = QuickLookFolderScan.Run(root.FullName, _ => updates++, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal(new QuickLookFolderTotals(15, 2, 1, false, true), totals);
            Assert.True(updates >= 1);
        }
        finally { root.Delete(recursive: true); }
    }
}
