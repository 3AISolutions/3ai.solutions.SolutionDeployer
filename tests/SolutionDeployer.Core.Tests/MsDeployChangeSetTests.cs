using SolutionDeployer.Core.Backup;

namespace SolutionDeployer.Core.Tests;

public sealed class MsDeployChangeSetTests
{
    [Fact]
    public void Parses_updates_deletes_and_adds()
    {
        var changes = MsDeployChangeSet.Parse(
        [
            "Info: Using ID 'abc' for connections to the remote server.",
            @"Info: Updating file (api.site.net\bin\App.dll).",
            @"Info: Updating file (api.site.net\Web.config).",
            @"Info: Deleting file (api.site.net\bin\Old.dll).",
            @"Info: Adding file (api.site.net\bin\New.dll).",
            @"Info: Updating directory (api.site.net\bin).",
            "Total changes: 4 (1 added, 1 deleted, 2 updated, 0 parameters changed, 1234 bytes copied)",
        ]);

        Assert.Equal([@"api.site.net\bin\App.dll", @"api.site.net\Web.config"], changes.Updated);
        Assert.Equal([@"api.site.net\bin\Old.dll"], changes.Deleted);
        Assert.Equal([@"api.site.net\bin\New.dll"], changes.Added);
        Assert.Equal(3, changes.ToSave.Count);
        Assert.False(changes.LooksUnparsed);
    }

    [Fact]
    public void Directory_changes_cover_their_children()
    {
        var changes = MsDeployChangeSet.Parse(
        [
            @"Info: Deleting file (site/Old\a.txt).",
            @"Info: Deleting directory (site/Old).",
            @"Info: Deleting file (site/Old\Deep\b.txt).",
            @"Info: Deleting directory (site/Old\Deep).",
            @"Info: Adding directory (site\New).",
            @"Info: Adding child filePath (site\New\c.txt).",
        ]);

        Assert.Equal(["site/Old"], changes.Deleted);
        Assert.Equal([@"site\New"], changes.Added);
    }

    [Fact]
    public void No_changes_is_empty_and_parsed()
    {
        var changes = MsDeployChangeSet.Parse(["Total changes: 0 (0 added, 0 deleted, 0 updated, 0 parameters changed, 0 bytes copied)"]);

        Assert.True(changes.IsEmpty);
        Assert.False(changes.LooksUnparsed);
    }

    [Fact]
    public void Reported_changes_without_recognised_lines_look_unparsed()
    {
        var changes = MsDeployChangeSet.Parse(["Something unexpected (x)", "Total changes: 3 (1 added, 0 deleted, 2 updated)"]);

        Assert.True(changes.LooksUnparsed);
    }
}
