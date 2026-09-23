// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text.Json;

namespace A2Utils.Cli.Tests;

public sealed class Cc65WorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-cc-cli-{Guid.NewGuid():N}");
    public Cc65WorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Compile_ValidCompilerOutput_StagesAppleSingleAndReportsMetadata()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }\n");
        var result = Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("hello.as"), "--json");
        Assert.Equal(0, result.Code);
        using JsonDocument json = JsonDocument.Parse(result.Output);
        Assert.Equal("cc.compile", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(0x0803, json.RootElement.GetProperty("data").GetProperty("auxType").GetInt32());
        Assert.True(File.Exists(At("hello.as")));
        Assert.False(File.Exists(At("main.o")));
    }

    [Theory]
    [InlineData("A2_FAKE_FAILURE")]
    [InlineData("A2_FAKE_INVALID")]
    public void Compile_Failure_PreservesExistingOutputAndSource(string marker)
    {
        string source = "// " + marker + "\nint main(void) { return 0; }\n";
        File.WriteAllText(At("main.c"), source);
        File.WriteAllText(At("hello.as"), "previous output");
        var result = Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("hello.as"), "--overwrite", "--json");
        Assert.NotEqual(0, result.Code);
        Assert.Empty(result.Output);
        Assert.Equal(source, File.ReadAllText(At("main.c")));
        Assert.Equal("previous output", File.ReadAllText(At("hello.as")));
        Assert.Empty(Directory.GetFiles(_directory, ".*.a2-*"));
    }

    [Theory]
    [InlineData("#include \"header.h\"\n")]
    [InlineData("#/**/include/**/\"header.h\"\n")]
    [InlineData("#inc\\\nlude\"header.h\"\n")]
    public void Compile_SourceOrHeaderAlias_RefusesEvenWithOverwrite(string include)
    {
        File.WriteAllText(At("main.c"), include + "int main(void) { return VALUE; }\n");
        File.WriteAllText(At("header.h"), "#define VALUE 0\n");
        Assert.Equal(6, Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("main.c"), "--overwrite").Code);
        Assert.Equal(6, Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("header.h"), "--overwrite").Code);
        Assert.Equal("#define VALUE 0\n", File.ReadAllText(At("header.h")));
    }

    [Fact]
    public void Compile_IncbinDependencyAlias_RefusesEvenWithOverwrite()
    {
        File.WriteAllText(At("main.s"), "data: .incbin\"input.bin\",0,2\n");
        File.WriteAllBytes(At("input.bin"), [0x01, 0x02]);
        Assert.Equal(6, Run("cc", "compile", At("main.s"), "--compiler", HostPath(), "--to", At("input.bin"), "--overwrite").Code);
        Assert.Equal(new byte[] { 0x01, 0x02 }, File.ReadAllBytes(At("input.bin")));
    }

    [Fact]
    public void Compile_ExistingOutput_RequiresOverwrite()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }\n");
        File.WriteAllText(At("hello.as"), "previous output");
        Assert.Equal(6, Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("hello.as")).Code);
        Assert.Equal("previous output", File.ReadAllText(At("hello.as")));
        Assert.Equal(0, Run("cc", "compile", At("main.c"), "--compiler", HostPath(), "--to", At("hello.as"), "--overwrite").Code);
    }

    private static string HostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    private string At(string name) => Path.Combine(_directory, name);
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int code = CliApplication.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
