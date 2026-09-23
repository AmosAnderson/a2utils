// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Graphics;

namespace A2Utils.Core.Tests;

public sealed class ExecutionGraphicsMemoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-graphics-memory-" + Guid.NewGuid().ToString("N"));

    public ExecutionGraphicsMemoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Run_ExpectedLoresMemory_PassesAndWritesPixelEvidence()
    {
        string expected = WritePng("black.png", 40, 48);
        string source = At("routine.asm");
        File.WriteAllText(source, "rts\n");
        ExecutionSpec spec = new()
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = source },
            GraphicsMemory = [new(expected, "lores")]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("run"));

        Assert.True(result.Passed, string.Join('\n', result.Diagnostics.Select(item => item.Message)));
        ExecutionGraphicsMemoryResult comparison = Assert.Single(result.GraphicsMemory);
        Assert.True(comparison.Passed);
        Assert.Equal(0, comparison.MismatchedBytes);
        Assert.Equal(0, comparison.Pixels!.DifferentPixels);
        Assert.True(File.Exists(comparison.ExpectedPreview));
        Assert.True(File.Exists(comparison.ActualPreview));
        Assert.True(File.Exists(comparison.DifferenceImage));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(At("run/result.json")));
        Assert.True(json.RootElement.GetProperty("graphicsMemory")[0].GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Run_ChangedDisplayByte_FailsWithMemoryAndPixelLocation()
    {
        string expected = WritePng("black.png", 40, 48);
        string source = At("routine.asm");
        File.WriteAllText(source, "lda #$11\nsta $0400\nrts\n");
        ExecutionSpec spec = new()
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = source },
            GraphicsMemory = [new(expected, "lores")]
        };

        ExecutionResult result = await ExecutionRunner.RunAsync(spec, At("failed"));

        Assert.False(result.Passed);
        ExecutionGraphicsMemoryResult comparison = Assert.Single(result.GraphicsMemory);
        Assert.False(comparison.Passed);
        Assert.Equal(1, comparison.MismatchedBytes);
        Assert.Equal("cpu:$0400", comparison.FirstMismatch);
        Assert.True(comparison.Pixels!.DifferentPixels > 0);
        Assert.Contains(result.Diagnostics, item => item.Code == "execution.graphics_memory");
    }

    [Fact]
    public async Task Run_CancelledWithGraphicsAssertion_RetainsResultAndComparisonEvidence()
    {
        string expected = WritePng("black.png", 40, 48);
        string source = At("routine.asm");
        File.WriteAllText(source, "loop: jmp loop\n");
        ExecutionSpec spec = new()
        {
            Engine = "cpu",
            Machine = "apple2e",
            Routine = new() { Source = source, MaxCycles = 1_000_000_000 },
            GraphicsMemory = [new(expected, "lores")]
        };
        using CancellationTokenSource cancellation = new();

        Task<ExecutionResult> running = ExecutionRunner.RunAsync(spec, At("cancelled"), cancellation.Token);
        cancellation.Cancel();
        ExecutionResult result = await running;

        Assert.False(result.Passed);
        Assert.Equal("cancelled", result.StopReason);
        ExecutionGraphicsMemoryResult comparison = Assert.Single(result.GraphicsMemory);
        Assert.True(File.Exists(comparison.ExpectedPreview));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(At("cancelled/result.json")));
        Assert.Equal("cancelled", json.RootElement.GetProperty("stopReason").GetString());
        Assert.Single(json.RootElement.GetProperty("graphicsMemory").EnumerateArray());
    }

    [Fact]
    public void Prepare_DoubleHiresStep_ExpandsToExplicitAuxiliaryAndMainPages()
    {
        string expected = WritePng("dhires.png", 560, 192);
        ExecutionSpec spec = new()
        {
            Machine = "apple2ee",
            Steps = [new()
            {
                Name = "screen ready",
                Action = "assert",
                Condition = new() { GraphicsMemory = [new(expected, "dhires-mono", Page: 2)] }
            }]
        };

        PreparedGraphicsExecution prepared = ExecutionGraphicsMemory.Prepare(spec);

        Assert.Empty(prepared.Spec.Steps[0].Condition!.GraphicsMemory);
        MemoryAssertion[] memory = prepared.Spec.Steps[0].Condition!.Memory.ToArray();
        Assert.Collection(memory,
            auxiliary => { Assert.Equal("aux", auxiliary.Bank); Assert.Equal(0x4000, auxiliary.Address); Assert.Equal(16384, auxiliary.Hex.Length); },
            main => { Assert.Equal("main", main.Bank); Assert.Equal(0x4000, main.Address); Assert.Equal(16384, main.Hex.Length); });
    }

    [Fact]
    public void Attach_ExpectedImageDeletedAfterPreparation_RetainsFailedComparisonEvidence()
    {
        string expected = WritePng("deleted.png", 40, 48);
        PreparedGraphicsExecution prepared = ExecutionGraphicsMemory.Prepare(new()
        {
            Machine = "apple2e",
            GraphicsMemory = [new(expected, "lores")]
        });
        PreparedGraphicsSegment segment = Assert.Single(Assert.Single(prepared.Assertions).Segments);
        string artifacts = At("deleted-evidence");
        Directory.CreateDirectory(artifacts);
        File.Delete(expected);
        ExecutionResult execution = new(1, "deleted expected image", true, "return", null, null,
            new Dictionary<string, long>(), new Dictionary<int, string>
            {
                [segment.Address] = Convert.ToHexString(segment.Bytes)
            }, null, null, artifacts, [], []);

        ExecutionResult result = ExecutionGraphicsMemory.Attach(execution, prepared);

        Assert.False(result.Passed);
        ExecutionGraphicsMemoryResult comparison = Assert.Single(result.GraphicsMemory);
        Assert.False(comparison.Passed);
        Assert.Equal(0, comparison.MismatchedBytes);
        Assert.True(File.Exists(comparison.ExpectedPreview));
        Assert.True(File.Exists(comparison.ActualPreview));
        Assert.True(File.Exists(comparison.DifferenceImage));
        Assert.Contains(result.Diagnostics, item => item.Code == "execution.graphics_changed");
    }

    [Fact]
    public void Load_GraphicsReferences_ResolveRelativeToSpecification()
    {
        WritePng("expected.png", 40, 48);
        File.WriteAllText(At("spec.json"), """
            {
              "schemaVersion": 1,
              "graphicsMemory": [{ "expectedImage": "expected.png", "mode": "lores" }],
              "steps": [{
                "name": "screen",
                "action": "assert",
                "condition": { "graphicsMemory": [{ "expectedImage": "expected.png", "mode": "lores" }] }
              }]
            }
            """);

        ExecutionSpec spec = ExecutionSpec.Load(At("spec.json"));

        Assert.Equal(At("expected.png"), spec.GraphicsMemory[0].ExpectedImage);
        Assert.Equal(At("expected.png"), spec.Steps[0].Condition!.GraphicsMemory[0].ExpectedImage);
    }

    private string WritePng(string name, int width, int height)
    {
        string path = At(name);
        File.WriteAllBytes(path, PngCodec.Encode(new(width, height, new byte[width * height * 3])));
        return path;
    }

    private string At(string path) => Path.GetFullPath(path, _directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
