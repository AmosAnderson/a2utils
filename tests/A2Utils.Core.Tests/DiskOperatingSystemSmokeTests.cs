// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Security.Cryptography;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class DiskOperatingSystemSmokeTests
{
    [DiskOperatingSystemSmokeFact("A2_DOS33_SMOKE_DISK")]
    public Task Run_RealDos33_CatalogsLoadsAndSavesKnownBinary()
        => RunAsync("dos33", "A2_DOS33_SMOKE_DISK");

    [DiskOperatingSystemSmokeFact("A2_PRODOS_SMOKE_DISK")]
    public Task Run_RealProdos_CatalogsLoadsAndSavesKnownBinary()
        => RunAsync("prodos", "A2_PRODOS_SMOKE_DISK");

    private static async Task RunAsync(string fileSystem, string diskVariable)
    {
        using FixtureWorkspace workspace = new();
        string source = Environment.GetEnvironmentVariable(diskVariable)!;
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
        AssemblyResult program = Assembler.Assemble("""
            .org $2000
            ldx #7
            copy: lda message,x
            sta $0300,x
            dex
            bpl copy
            rts
            message: .byte $41,$32,$44,$49,$53,$4b,$4f,$4b
            """);
        string order;
        string staged;
        using (DiskSession disk = DiskSession.Open(source))
        {
            DiskInfo info = disk.Info;
            Assert.Equal(fileSystem, info.FileSystem);
            Assert.Equal(143360L, info.SizeBytes);
            Assert.True(disk.List().Count <= 11, "Use a boot template with at most eleven existing root entries so CATALOG fits on one text screen.");
            order = info.Order;
            string extension = info.Container == "2mg" ? ".2mg" : order == "dos" ? ".do" : ".po";
            staged = workspace.NewPath("os-smoke" + extension);
        }
        ImageTransactions.Write(source, staged, false, false, temporary =>
        {
            using DiskSession disk = DiskSession.Open(temporary, writable: true);
            disk.Add("A2.SMOKE", program.Bytes, fileSystem == "dos33" ? "B" : "BIN", 0x2000);
        }, temporary =>
        {
            using DiskSession disk = DiskSession.Open(temporary);
            Assert.False(disk.Info.IsDubious);
            Assert.DoesNotContain(disk.Verify(), diagnostic => diagnostic.Severity == "error");
        });
        ExecutionSpec spec = new()
        {
            Name = $"Real {fileSystem} catalog/load/save interoperability",
            EmulatorPath = Environment.GetEnvironmentVariable("A2_MAME_PATH")!,
            RomDirectory = Environment.GetEnvironmentVariable("A2_MAME_ROMS")!,
            Machine = "apple2ee",
            Disks = [new("flop1", staged, InputOrder: order, InputFileSystem: fileSystem, Verify: true)],
            EmulatedSeconds = 25,
            HostTimeoutSeconds = 60,
            Keys = [new(12, "BLOAD A2.SMOKE\r"), new(14, "CALL 8192\r"),
                new(16, "BSAVE A2.RESULT,A768,L8\r")],
            Until = new(0x307, 0x4B, 20),
            Memory = [new(0x300, "41324449534B4F4B")],
            DiskAssertions = [new("flop1", "A2.RESULT", Hex: "41324449534B4F4B", Type: "BIN", AuxType: 0x300, Length: 8)],
            Screenshot = true,
            Trace = true
        };
        // Observe CATALOG before any command echoes A2.SMOKE: a BLOAD echo is not catalog evidence.
        ExecutionSpec catalogSpec = spec with
        {
            Name = $"Real {fileSystem} catalog interoperability",
            EmulatedSeconds = 15,
            Keys = [new(10, "CATALOG\r")],
            Until = null,
            Memory = [],
            DiskAssertions = [],
            TextContains = ["A2.SMOKE"]
        };
        ExecutionResult catalog = await ExecutionRunner.RunAsync(catalogSpec, workspace.NewPath("catalog-evidence"));
        Assert.True(catalog.Passed, "CATALOG: " + string.Join("\n", catalog.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}; expected {diagnostic.Expected}; actual {diagnostic.Actual}")));
        ExecutionResult result = await ExecutionRunner.RunAsync(spec, workspace.NewPath("evidence"));
        Assert.True(result.Passed, "LOAD/SAVE: " + string.Join("\n", result.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}; expected {diagnostic.Expected}; actual {diagnostic.Actual}")));
        Assert.Equal("completion_condition", result.StopReason);
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
    }
}

public sealed class DiskOperatingSystemSmokeFactAttribute : FactAttribute
{
    public DiskOperatingSystemSmokeFactAttribute(string diskVariable)
    {
        if (new[] { "A2_MAME_PATH", "A2_MAME_ROMS", diskVariable }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = $"Set A2_MAME_PATH, A2_MAME_ROMS, and {diskVariable} for the optional real OS catalog/load/save test.";
    }
}
