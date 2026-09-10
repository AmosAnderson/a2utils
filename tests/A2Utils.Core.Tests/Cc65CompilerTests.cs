using System.Diagnostics;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Tests;

public sealed class Cc65CompilerTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-cc65-tests-{Guid.NewGuid():N}");
    public Cc65CompilerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Compile_IsolatedCopy_PreservesAdjacentSourceAndObjectAndReportsHashes()
    {
        File.WriteAllText(At("main.c"), "#include \"value.h\"\nint main(void) { return VALUE; }\n");
        File.WriteAllText(At("value.h"), "#define VALUE 0\n");
        File.WriteAllText(At("main.s"), "original assembly");
        File.WriteAllText(At("main.o"), "original object");
        Cc65Result result = Cc65Compiler.Compile(At("main.c"), Options(), _directory);
        Assert.Equal("original assembly", File.ReadAllText(At("main.s")));
        Assert.Equal("original object", File.ReadAllText(At("main.o")));
        Assert.Equal(0x0803, AppleSingleProgram.Decode(result.AppleSingle).AuxType);
        Assert.Contains(result.Inputs, i => i.Path == "value.h" && i.Sha256 == ProgramFiles.Hash(File.ReadAllBytes(At("value.h"))));
        Assert.Contains("._main", result.Labels);
        Assert.DoesNotContain("a2-cc65-", result.Map);
        Assert.Equal("cl65 V0.0 - contract test double", result.Version);
    }

    [Fact]
    public void Compile_WrongVersion_RefusesBeforeCompile()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }");
        DiskException error = Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.c"), Options() with { ExpectedVersion = "wrong" }, _directory));
        Assert.Equal("cc65.version_mismatch", error.Code);
        Assert.False(File.Exists(At("main.o")));
    }

    [Theory]
    [InlineData("A2_FAKE_FAILURE", "cc65.compile_failed")]
    [InlineData("A2_FAKE_INVALID", "applesingle.invalid")]
    public void Compile_CompilerOrOutputFailure_RejectsAndPreservesInput(string marker, string code)
    {
        string source = "// " + marker + "\nint main(void) { return 0; }";
        File.WriteAllText(At("main.c"), source);
        Assert.Equal(code, Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.c"), Options(), _directory)).Code);
        Assert.Equal(source, File.ReadAllText(At("main.c")));
        Assert.False(File.Exists(At("main.o")));
    }

    [Theory]
    [InlineData("#include \"../outside.h\"\n")]
    [InlineData("#include HEADER\n")]
    public void Compile_UnsupportedIncludes_RejectBeforeCompiler(string source)
    {
        File.WriteAllText(At("main.c"), source);
        Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.c"), Options(), _directory));
        Assert.False(File.Exists(At("main.o")));
    }

    [Theory]
    [InlineData("#include/**/\"../outside.h\"\n")]
    [InlineData("#/**/include\"../outside.h\"\n")]
    [InlineData("#inc\\\nlude \"../outside.h\"\n")]
    [InlineData("#inc\\\r\nlude \"../outside.h\"\n")]
    [InlineData("#/* comment\n continued */include \"../outside.h\"\n")]
    [InlineData("#include\"../outside.h\"\n")]
    [InlineData("\v\f#\vinclude\"../outside.h\"\n")]
    [InlineData("#include \"value.bin\"\n")]
    [InlineData("#include HEADER\n")]
    public void Compile_CDirectiveVariants_RejectEscapesBeforeCompiler(string source)
    {
        File.WriteAllText(At("main.c"), source);
        DiskException error = Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.c"), Options(), _directory));
        Assert.StartsWith("cc65.", error.Code);
        Assert.False(File.Exists(At("main.o")));
    }

    [Theory]
    [InlineData("label: .include\"../outside.inc\"\n")]
    [InlineData("label: .INCBIN \"../outside.bin\",0,1\n")]
    [InlineData(".inc\\\nlude \"../outside.inc\"\n")]
    [InlineData(".include .concat(\"..\",\"/outside.inc\")\n")]
    [InlineData(".include \"value.bin\"\n")]
    [InlineData(".include \"value.h\"\n")]
    [InlineData(".feature string_escapes\n")]
    [InlineData(".feature missing_char_term\n")]
    [InlineData(".linecont +\n")]
    [InlineData("; comment \\\n.include \"../outside.inc\"\n")]
    [InlineData(".include \"..\\outside.inc\"\n")]
    [InlineData(".byte \"\\\"\n.include \"../outside.inc\"\n")]
    public void Compile_AssemblyDirectiveVariants_RejectEscapesBeforeCompiler(string source)
    {
        File.WriteAllText(At("main.s"), source);
        DiskException error = Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.s"), Options(), _directory));
        Assert.StartsWith("cc65.", error.Code);
    }

    [Fact]
    public void Compile_CommentedIncludesAndLiteralStrings_DoNotBecomeDirectives()
    {
        File.WriteAllText(At("main.c"), """
            /* #include "../outside.h" */
            // #include "../outside.h"
            const char *text = "#include \"../outside.h\" and asm";
            #/**/include/**/"value.h"
            int main(void) { return VALUE; }
            """);
        File.WriteAllText(At("value.h"), "#define VALUE 0\n");
        Cc65Result result = Cc65Compiler.Compile(At("main.c"), Options(), _directory);
        Assert.Contains(result.Inputs, input => input.Path == "value.h");
    }

    [Fact]
    public void Compile_AssemblyCommentsStringsAndLiteralIncbin_PreserveDataAndHashIt()
    {
        File.WriteAllText(At("main.s"), """
            ; .include "../outside.inc"
            ; comment ending in a literal backslash \
            .byte ".include ../outside.inc"
            label: .include"value.inc"
            data: .incbin "value.bin",0,1
            """);
        File.WriteAllText(At("value.inc"), "; asm and .include ../outside.inc\n.byte 1\n");
        File.WriteAllBytes(At("value.bin"), [0xff, 0x00]);
        Cc65Result result = Cc65Compiler.Compile(At("main.s"), Options(), _directory);
        Assert.Contains(result.Inputs, input => input.Path == "value.bin"
            && input.Sha256 == ProgramFiles.Hash(new byte[] { 0xff, 0x00 }));
    }

    [Fact]
    public void Build_Cc65ProjectOutsideWorkingDirectory_ResolvesDependencyHashesAgainstManifest()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }\n");
        ProjectManifest manifest = new()
        {
            Target = "apple2plus",
            Output = "build.po",
            Cc65 = Options(),
            Files = [new() { Source = "main.c", Path = "HELLO", Kind = "cc65" }]
        };
        File.WriteAllText(At("project.json"), System.Text.Json.JsonSerializer.Serialize(manifest, ProjectJson.Options));
        ProjectBuildResult result = ProjectBuilder.Build(At("project.json"));
        Assert.True(File.Exists(At("build.po")));
        Assert.Contains(result.Inputs, input => input.Path == At("main.c")
            && input.Sha256 == ProgramFiles.Hash(File.ReadAllBytes(At("main.c"))));
        Assert.All(result.Inputs, input => Assert.True(Path.IsPathFullyQualified(input.Path)));
    }

    [Fact]
    public void Compile_MissingCompiler_ReturnsActionableCode()
    {
        File.WriteAllText(At("main.c"), "int main(void) { return 0; }");
        Assert.Equal("cc65.compiler_missing", Assert.Throws<DiskException>(() =>
            Cc65Compiler.Compile(At("main.c"), Options() with { Compiler = At("missing.exe") }, _directory)).Code);
    }

    [Fact]
    public void Compile_Timeout_TerminatesCompilerChildAndPreservesInput()
    {
        File.WriteAllText(At("main.c"), "// A2_FAKE_SLEEP\n// PID " + At("child.pid") + "\n");
        DiskException error = Assert.Throws<DiskException>(() => Cc65Compiler.Compile(At("main.c"), Options() with { TimeoutSeconds = 1 }, _directory));
        Assert.Equal("cc65.timeout", error.Code);
        AssertExited(At("child.pid"));
        Assert.False(File.Exists(At("main.o")));
    }

    [Fact]
    public async Task Compile_Cancellation_TerminatesCompilerChild()
    {
        File.WriteAllText(At("main.c"), "// A2_FAKE_SLEEP\n// PID " + At("child.pid") + "\n");
        using CancellationTokenSource cancellation = new();
        Task<Cc65Result> running = Task.Run(() => Cc65Compiler.Compile(At("main.c"), Options(), _directory, cancellation.Token));
        Stopwatch watch = Stopwatch.StartNew();
        while (!File.Exists(At("child.pid")) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        AssertExited(At("child.pid"));
    }

    [Fact]
    public void ResolvePhysicalDirectory_LinkedAncestor_ReturnsUnlinkedPath()
    {
        string physical = At("physical");
        string nested = Path.Combine(physical, "nested");
        Directory.CreateDirectory(nested);
        string alias = At("alias");
        try
        {
            Directory.CreateSymbolicLink(alias, physical);
        }
        catch (Exception exception) when (OperatingSystem.IsWindows()
            && exception is IOException or UnauthorizedAccessException)
        {
            // Windows requires either Developer Mode or link-creation privilege.
            return;
        }

        string resolved = Cc65Compiler.ResolvePhysicalDirectory(Path.Combine(alias, "nested"));

        Assert.Equal(nested, resolved);
        ImageTransactions.ValidatePath(resolved);
    }

    [Fact]
    public async Task CleanupGeneratedDirectory_LockedFile_DoesNotMaskOperation()
    {
        string temporary = At("cleanup");
        Directory.CreateDirectory(temporary);
        string child = Path.Combine(temporary, "locked.bin");
        await File.WriteAllBytesAsync(child, [1, 2, 3]);

        using (FileStream locked = new(child, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Cc65Compiler.CleanupGeneratedDirectoryAsync(temporary);
        }

        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void Compile_InvalidTargetAndOptions_RejectBeforeExecution()
    {
        Assert.Equal("cc65.target", Assert.Throws<DiskException>(() => Cc65Compiler.Compile("main.c", Options() with { Target = "c64" }, _directory)).Code);
        Assert.Equal("cc65.timeout", Assert.Throws<DiskException>(() => Cc65Compiler.Compile("main.c", Options() with { TimeoutSeconds = 0 }, _directory)).Code);
        Assert.Equal("cc65.define", Assert.Throws<DiskException>(() => Cc65Compiler.Compile("main.c", Options() with { Defines = ["-o=other"] }, _directory)).Code);
    }

    private static Cc65Options Options() => new() { Compiler = HostPath(), ExpectedVersion = "cl65 V0.0 - contract test double" };
    private static string HostPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root!.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    private static void AssertExited(string path)
    {
        Assert.True(File.Exists(path));
        int pid = int.Parse(File.ReadAllText(path));
        try
        {
            using Process process = Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000));
        }
        catch (ArgumentException) { }
    }

    private string At(string name) => Path.Combine(_directory, name);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
