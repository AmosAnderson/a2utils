// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class IieBankLoaderTests
{
    [Fact]
    public void Assemble_BankLoaderExample_FitsSingleBootSectorAndChecksProject()
    {
        using FixtureWorkspace workspace = new();
        AssemblyResult program = AssembleBoot(workspace);
        Assert.InRange(program.Bytes.Length, 1, 256);
        Assert.Contains("copy_page_to_aux", program.Symbols.Keys);
        Assert.Contains("copy_page_to_lc1", program.Symbols.Keys);
        ProjectBuildResult build = ProjectBuilder.Build(Example("project.a2.json"), checkOnly: true);
        Assert.Equal("main", Assert.Single(build.Memory).MemoryBank);
        Assert.Equal(0x2000, build.Files[0].Origin);
    }

    [MameSmokeFact]
    public async Task Run_BankLoaderExample_CopiesPhysicalBanksAndPreservesOtherBankContents()
    {
        using FixtureWorkspace workspace = new();
        AssemblyResult program = AssembleBoot(workspace);
        Assert.InRange(program.Bytes.Length, 1, 256);
        byte[] disk = new byte[143360];
        program.Bytes.CopyTo(disk, 0);
        string input = workspace.NewPath("boot.dsk");
        File.WriteAllBytes(input, disk);
        string pattern = Convert.ToHexString(Enumerable.Range(0, 256).Select(value => (byte)value).ToArray());
        ExecutionResult result = await ExecutionRunner.RunAsync(new()
        {
            Name = "Apple IIe main, auxiliary and language-card copy routines",
            EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
            RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
            Machine = "apple2ee",
            DiskImage = input,
            EmulatedSeconds = 6,
            HostTimeoutSeconds = 30,
            Until = new(0x300, 0x2a, 2, "main"),
            Memory = [new(0x300, "2A8000", "main"), new(0x3000, pattern, "main"),
                new(0x4000, "A5", "main"), new(0x4000, pattern, "aux"),
                new(0xd000, pattern, "lc1"), new(0xd000, "A6", "lc2")]
        }, workspace.NewPath("run"));
        Assert.True(result.Passed, string.Join("\n", result.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Message}; expected {diagnostic.Expected}; actual {diagnostic.Actual}")));
        Assert.Equal(disk, File.ReadAllBytes(input));
    }

    private static AssemblyResult AssembleBoot(FixtureWorkspace workspace)
    {
        File.Copy(Example("bank-copy.inc"), workspace.NewPath("bank-copy.inc"));
        File.Copy(Example("bank-demo.inc"), workspace.NewPath("bank-demo.inc"));
        string source = workspace.NewPath("boot.asm");
        File.WriteAllText(source, """
            .org $0800
            .byte 1
            JSR demo
            done: JMP done
            demo:
            .include "bank-demo.inc"
            """);
        return Assembler.AssembleFile(source);
    }

    private static string Example(string name) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(FixtureWorkspace.FindFixture("independent-dos33.do"))!,
        "..", "..", "examples", "development", "iie-banks", name));
}
