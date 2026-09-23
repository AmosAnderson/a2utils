// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class DevelopmentExampleTests
{
    [Theory]
    [InlineData("keyboard", "KEYECHO", 0x2000, "2058FC")]
    [InlineData("hires", "STRIPES", 0x6000, "A955A000")]
    public void Build_AssemblyExample_StoresKnownEntryBytesAndInferredLoadAddress(
        string example, string path, int origin, string prefix)
    {
        using FixtureWorkspace workspace = new();
        string image = workspace.NewPath("example.po");

        ProjectBuildResult result = ProjectBuilder.Build(FindExample(example + ".a2.json"), image);

        BuiltFile file = Assert.Single(result.Files);
        Assert.Equal(origin, file.Origin);
        Assert.Equal(origin, file.Symbols["start"]);
        using DiskSession disk = DiskSession.Open(image);
        Assert.Equal(origin, disk.GetEntry(path).AuxType);
        Assert.True(disk.ReadFile(path).AsSpan().StartsWith(Convert.FromHexString(prefix)));
    }

    [Fact]
    public void Build_MixedExample_LoadAddressMatchesBasicCallAndKeepsRegionsSeparate()
    {
        using FixtureWorkspace workspace = new();
        string image = workspace.NewPath("mixed.po");

        ProjectBuildResult result = ProjectBuilder.Build(FindExample("mixed.a2.json"), image);

        Assert.Equal(2, result.Files.Count);
        using DiskSession disk = DiskSession.Open(image);
        Assert.Equal(0x2000, disk.GetEntry("ROUTINE").AuxType);
        string basic = ApplesoftBasic.Decompile(disk.ReadFile("DEMO"));
        Assert.Contains("BLOAD ROUTINE", basic);
        Assert.Contains("CALL 8192", basic);
        MemoryRegion basicMemory = Assert.Single(result.Memory, region => region.Name == "DEMO");
        Assert.True(basicMemory.Start + basicMemory.Length <= 0x2000);
    }

    [Fact]
    public void Build_DiskExample_ContainsCanonicalReadAndWriteProgram()
    {
        using FixtureWorkspace workspace = new();
        string image = workspace.NewPath("disk.po");

        ProjectBuilder.Build(FindExample("disk.a2.json"), image);

        using DiskSession disk = DiskSession.Open(image);
        byte[] bytes = disk.ReadFile("DISKDEMO");
        string basic = ApplesoftBasic.Decompile(bytes);
        Assert.Contains("WRITE SAMPLE.DATA", basic);
        Assert.Contains("READ SAMPLE.DATA", basic);
        Assert.Equal(bytes, ApplesoftBasic.Compile(basic));
    }

    private static string FindExample(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "examples", "development", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Development examples are unavailable.", name);
    }
}
