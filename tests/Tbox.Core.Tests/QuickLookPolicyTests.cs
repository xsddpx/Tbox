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
    [InlineData("a.7z", false, QuickLookKind.Archive)]
    [InlineData("a.RAR", false, QuickLookKind.Archive)]
    [InlineData("Program.cs", false, QuickLookKind.Text)]
    [InlineData("config.yaml", false, QuickLookKind.Text)]
    [InlineData("Dockerfile", false, QuickLookKind.Text)]
    [InlineData("README.md", false, QuickLookKind.Markdown)]
    [InlineData("logo.svg", false, QuickLookKind.Svg)]
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
            AlwaysOnTop: false, CloseAfterLosingFocus: false, WrapText: false, TextFontSize: 14, MaxTextFileSizeKb: 10240 });
        Assert.Equal((2, 100, 1, 102400), (QuickLookPolicy.ClampFontSize(0), QuickLookPolicy.ClampFontSize(500), QuickLookPolicy.ClampMaxTextKb(0), QuickLookPolicy.ClampMaxTextKb(int.MaxValue)));
        var settings = new AppSettings { QuickLook = defaults with { SpaceToActivate = false, AlwaysOnTop = true } };
        Assert.Equal(settings, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)));
    }

    [Fact]
    public void TextSniffingAcceptsUtf8AndRejectsBinary()
    {
        Assert.True(QuickLookPolicy.LooksLikeText("hello 世界\n"u8));
        // A multi-byte character cut by the 4 KB sample is still text.
        Assert.True(QuickLookPolicy.LooksLikeText("ab世"u8[..4]));
        Assert.True(QuickLookPolicy.LooksLikeText(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }));
        Assert.False(QuickLookPolicy.LooksLikeText(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }));
        // GBK bytes are not valid UTF-8, so sniffing alone does not call them text.
        Assert.False(QuickLookPolicy.LooksLikeText(new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }));
    }

    [Fact]
    public void ReadTextHonoursByteOrderMarksFallsBackToAnsiAndCuts()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbk = System.Text.Encoding.GetEncoding(936);
        var path = Path.Combine(Path.GetTempPath(), $"tbox-quicklook-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, "你好", new System.Text.UTF8Encoding(true));
            Assert.Equal(("你好", "UTF-8 BOM", false), QuickLookPolicy.ReadText(path, 1024, gbk));
            File.WriteAllText(path, "你好", System.Text.Encoding.Unicode);
            Assert.Equal(("你好", "UTF-16 LE", false), QuickLookPolicy.ReadText(path, 1024, gbk));
            File.WriteAllBytes(path, gbk.GetBytes("你好"));
            Assert.Equal(("你好", "GB2312", false), QuickLookPolicy.ReadText(path, 1024, gbk));
            File.WriteAllText(path, "abcdef");
            Assert.Equal(("abc", "UTF-8", true), QuickLookPolicy.ReadText(path, 3, gbk));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchiveBuilderAcceptsEntriesInAnyOrder()
    {
        var builder = new QuickLookArchiveBuilder();
        builder.Add("src\\b.cs", false, 4); builder.Add("a.txt", false, 1); builder.Add("src/", true, 0); builder.Add("src/A.cs", false, 2);
        var archive = builder.Build(10);
        Assert.Equal(["src", "a.txt"], archive.Root.Select(entry => entry.Name));
        Assert.Equal(["A.cs", "b.cs"], archive.Root[0].Children.Select(entry => entry.Name));
        Assert.Equal((1, 3, 7L, 10L), (archive.Directories, archive.Files, archive.ExtractedSize, archive.ArchiveSize));
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
