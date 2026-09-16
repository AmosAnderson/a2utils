using A2Utils.Core.Assembly;
using A2Utils.Core.Basic;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class RuntimeLibraryTests
{
    [Theory]
    [InlineData("text-keyboard.inc", "a2_puts")]
    [InlineData("text-keyboard.inc", "a2_getkey")]
    [InlineData("hgr-sprite.inc", "a2_hgr_sprite")]
    [InlineData("prodos-file.inc", "a2_file_open")]
    [InlineData("prodos-file.inc", "a2_file_read")]
    [InlineData("prodos-file.inc", "a2_file_write")]
    [InlineData("prodos-file.inc", "a2_file_close")]
    public void Assemble_RuntimeInclude_ExportsCallableEntry(string file, string entry)
    {
        AssemblyResult program = Assembler.Assemble(File.ReadAllText(Example(file)), 0x6000);
        Assert.InRange(program.Symbols[entry], 0x6000, 0x6000 + program.Bytes.Length - 1);
        Assert.InRange(program.Bytes.Length, 1, 1024);
    }

    [Fact]
    public void Assemble_ProDosFileCall_UsesInlineMliCommandAndParameterLayout()
    {
        AssemblyResult program = Assembler.Assemble(File.ReadAllText(Example("prodos-file.inc")), 0x6000);
        int command = program.Symbols["a2_file_command"] - program.Origin;
        int parameters = program.Symbols["a2_file_parameters"] - program.Origin;
        Assert.Equal(command + 1, parameters);
        Assert.Equal(new byte[] { 0x20, 0x00, 0xbf, 0, 0, 0, 0x60 }, program.Bytes[(command - 3)..]);
        Assert.Equal(0xc8, program.Bytes[program.Symbols["a2_file_open"] - program.Origin + 1]);
        Assert.Equal(0xca, program.Bytes[program.Symbols["a2_file_read"] - program.Origin + 1]);
        Assert.Equal(0xcb, program.Bytes[program.Symbols["a2_file_write"] - program.Origin + 1]);
        Assert.Equal(0xcc, program.Bytes[program.Symbols["a2_file_close"] - program.Origin + 1]);
    }

    [Theory]
    [InlineData("basic-call.a2.json")]
    [InlineData("asset-demo.a2.json")]
    public void Build_RuntimeExample_ChecksCodeAssetsAndMemoryContracts(string manifest)
    {
        ProjectBuildResult build = ProjectBuilder.Build(Example(manifest), checkOnly: true);
        Assert.True(build.CheckOnly);
        Assert.NotEmpty(build.Files);
        Assert.DoesNotContain(build.Diagnostics, diagnostic => diagnostic.Severity == "error");
        if (manifest == "asset-demo.a2.json")
        {
            Assert.Single(build.Assets);
            Assert.Equal(4, build.Assets[0].Outputs.Count);
            Assert.Equal(2, build.Files[0].Symbols["ATLAS_CELL_COUNT"]);
        }
    }

    [Theory]
    [InlineData("basic-call.bas")]
    [InlineData("dos-file.bas")]
    public void Compile_BasicExamples_PreservesExecutableLineNumbersAndControlFlow(string name)
    {
        string source = File.ReadAllText(Example(name));
        BasicCheckResult check = ApplesoftTools.Check(source);
        Assert.True(check.Valid, string.Join('\n', check.Diagnostics.Select(diagnostic => diagnostic.Message)));
        byte[] bytes = ApplesoftBasic.Compile(source);
        Assert.NotEmpty(bytes);
    }

    internal static string Example(string name) => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(FixtureWorkspace.FindFixture("independent-dos33.do"))!, "..", "..", "examples", "runtime", name));
}
