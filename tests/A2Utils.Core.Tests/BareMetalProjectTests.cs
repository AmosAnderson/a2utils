// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class BareMetalProjectTests
{
    [Fact]
    public void Build_BootAssembly_WritesValidatedTrackZeroSectorAndReportsBootability()
    {
        using FixtureWorkspace workspace = new();
        string source = workspace.NewPath("boot.asm");
        File.WriteAllText(source, ".org $0800\n.byte 1\nLDA #$2A\nSTA $0300\ndone: JMP done\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, """
            {
              "schemaVersion": 1,
              "output": "boot.do",
              "disk": { "fileSystem": "dos33", "order": "dos" },
              "files": [],
              "boot": { "source": "boot.asm", "kind": "asm", "origin": 2048, "sectors": 1 }
            }
            """);

        ProjectBuildResult result = ProjectBuilder.Build(project);

        Assert.Equal("self-booting-unverified", result.Bootability);
        Assert.NotNull(result.Boot);
        byte[] expected = new byte[256];
        Assembler.AssembleFile(source).Bytes.CopyTo(expected, 0);
        using DiskSession disk = DiskSession.Open(result.OutputPath);
        Assert.Equal(expected, disk.ReadBootSectors(1));
        Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
    }

    [Fact]
    public void Preflight_BootAssembly_MatchesCommittedImageHash()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("boot.asm"), ".org $0800\n.byte 1\nRTS\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, """
            {
              "schemaVersion": 1,
              "output": "boot.do",
              "disk": { "fileSystem": "dos33", "order": "dos" },
              "files": [],
              "boot": { "source": "boot.asm" }
            }
            """);

        ProjectBuildResult preview = ProjectBuilder.Build(project, preflight: true);
        ProjectBuildResult built = ProjectBuilder.Build(project);

        Assert.Equal(preview.Sha256, built.Sha256);
        Assert.False(preview.CheckOnly);
        Assert.True(preview.Preflight);
    }

    [Fact]
    public void Build_BootAssemblySourceLargerThanSectorCapacity_LimitsOutputInsteadOfSourceText()
    {
        using FixtureWorkspace workspace = new();
        string comments = string.Concat(Enumerable.Repeat(
            "; boot documentation may be much larger than the emitted sector\n", 100));
        File.WriteAllText(workspace.NewPath("boot.asm"), comments + ".org $0800\n.byte 1\nrts\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, """
            {
              "schemaVersion": 1,
              "output": "boot.do",
              "disk": { "fileSystem": "dos33", "order": "dos" },
              "files": [],
              "boot": { "source": "boot.asm" }
            }
            """);

        ProjectBuildResult result = ProjectBuilder.Build(project);

        Assert.True(new FileInfo(workspace.NewPath("boot.asm")).Length > 4096);
        Assert.Equal(2, result.Boot!.Length);
    }

    [Fact]
    public void Build_BootOnProdosGeometry_RefusesWithoutReplacingOutput()
    {
        using FixtureWorkspace workspace = new();
        File.WriteAllText(workspace.NewPath("boot.asm"), ".org $0800\nRTS\n");
        string project = workspace.NewPath("project.json");
        File.WriteAllText(project, """
            {
              "schemaVersion": 1,
              "output": "boot.po",
              "disk": { "fileSystem": "prodos", "order": "prodos" },
              "files": [],
              "boot": { "source": "boot.asm" }
            }
            """);
        string output = workspace.NewPath("existing.po");
        File.WriteAllBytes(output, [0x2a]);

        DiskException error = Assert.Throws<DiskException>(() => ProjectBuilder.Build(project, output, overwrite: true));

        Assert.Equal("project.boot_geometry", error.Code);
        Assert.Equal(new byte[] { 0x2a }, File.ReadAllBytes(output));
    }
}
