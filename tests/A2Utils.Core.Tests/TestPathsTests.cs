using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class TestPathsTests
{
    [Fact]
    public void TemporaryRoot_LinkedSystemTempDirectory_HasNoLinkedAncestors()
    {
        for (DirectoryInfo? directory = new(TestPaths.TemporaryRoot); directory is not null; directory = directory.Parent)
        {
            Assert.Null(directory.LinkTarget);
            Assert.Equal(0, (int)(directory.Attributes & FileAttributes.ReparsePoint));
        }
    }

    [Fact]
    public void ResolveDirectory_UnixLinkedAncestor_PermitsPhysicalWritesAndRejectsLinkedWrites()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using FixtureWorkspace workspace = new();
        string physical = workspace.NewPath("physical");
        string nested = Path.Combine(physical, "nested");
        Directory.CreateDirectory(nested);
        string alias = workspace.NewPath("alias");
        Directory.CreateSymbolicLink(alias, physical);
        string linkedDirectory = Path.Combine(alias, "nested");

        string resolved = TestPaths.ResolveDirectory(linkedDirectory);
        Assert.Equal(nested, resolved);
        string output = Path.Combine(resolved, "output.do");
        ImageTransactions.Create(output, false, temporary => File.WriteAllBytes(temporary, [1, 2, 3]));

        DiskException error = Assert.Throws<DiskException>(() =>
            ImageTransactions.Create(Path.Combine(linkedDirectory, "refused.do"), false,
                temporary => File.WriteAllBytes(temporary, [4, 5, 6])));

        Assert.Equal("write.link_refused", error.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
        Assert.False(File.Exists(Path.Combine(nested, "refused.do")));
    }
}
