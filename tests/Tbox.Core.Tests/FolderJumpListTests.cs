using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class FolderJumpListTests
{
    private static string Folder(params string[] segments) => Path.Combine([Path.GetTempPath(), .. segments]);

    [Fact]
    public void RecommendsAtMostThreeOpenDirectoriesInUseOrder()
    {
        var first = Folder("project", "素材");
        var second = Folder("archive", "素材");
        var third = Folder("design");
        var fourth = Folder("downloads");
        var closed = Folder("closed");
        var ordered = FolderJumpList.Recommend([fourth, first, second, third, first + Path.DirectorySeparatorChar],
            [closed, second, first + Path.DirectorySeparatorChar, third]);
        Assert.Equal([second, first, third], ordered);
    }

    [Fact]
    public void SearchesByNameOrFullPathAndKeepsSameNameDirectoriesSeparate()
    {
        var project = Folder("project", "素材");
        var archive = Folder("archive", "素材");
        Assert.Equal([project, archive], FolderJumpList.Search([project, archive, project], "素材"));
        Assert.Equal([archive], FolderJumpList.Search([project, archive], Folder("archive")));
    }

    [Fact]
    public void MovesRepeatedJumpsToTheFrontAndTogglesFavoritesByPath()
    {
        var project = Folder("project", "素材");
        var archive = Folder("archive", "素材");
        Assert.Equal([project, archive], FolderJumpList.Remember([archive, project], project));
        Assert.Equal([archive], FolderJumpList.ToggleFavorite([project, archive], project + Path.DirectorySeparatorChar));
    }
}
