using System.Xml.Linq;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class UiLayoutContractTests
{
    [Fact]
    public void SettingsPagesDisableHorizontalScrollingAndCenterTheirContent()
    {
        var document = XDocument.Load(RepositoryFile("src", "Tbox.App", "MainWindow.xaml"));
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var panelNames = new[] { "HomePanel", "GeneralPanel", "FolderJumpPanel", "ScreenPastePanel" };

        foreach (var panelName in panelNames)
        {
            var panel = document.Descendants()
                .Single(element => (string?)element.Attribute(xaml + "Name") == panelName);

            Assert.Equal("Stretch", (string?)panel.Attribute("HorizontalContentAlignment"));
            Assert.Equal("Disabled", (string?)panel.Attribute("HorizontalScrollMode"));
            Assert.Equal("Disabled", (string?)panel.Attribute("HorizontalScrollBarVisibility"));
            Assert.Equal("Center", (string?)panel.Elements().Single().Attribute("HorizontalAlignment"));
        }
    }

    [Fact]
    public void DisplayModeHandlerDoesNotForcePaneStateDuringNavigationTransition()
    {
        var source = File.ReadAllText(RepositoryFile("src", "Tbox.App", "MainWindow.xaml.cs"));
        var handlerStart = source.IndexOf("private void OnNavigationDisplayModeChanged", StringComparison.Ordinal);
        var nextHandler = source.IndexOf("private void OnSearchQuerySubmitted", handlerStart, StringComparison.Ordinal);

        Assert.True(handlerStart >= 0 && nextHandler > handlerStart);
        var handler = source[handlerStart..nextHandler];
        Assert.DoesNotContain("IsPaneOpen", handler, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tbox.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(relativeSegments).ToArray());
    }
}
