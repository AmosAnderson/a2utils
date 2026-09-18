using System.Text.Json;
using A2Utils.Core.Backends;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;
using A2Utils.Core.Setup;

namespace A2Utils.Core.Tests;

public sealed class DevelopmentEnvironmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, "a2-setup-" + Guid.NewGuid().ToString("N"));
    public DevelopmentEnvironmentTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Lock_ChangedContentAndAddedFile_InvalidatePinnedEnvironment()
    {
        string profile = WriteProfile();
        string lockFile = At("environment.lock.json");
        DevelopmentEnvironment.CreateLock(profile, lockFile);
        DevelopmentEnvironment.ValidateLock(profile, lockFile);
        File.WriteAllText(At("roms/apple2ee.rom"), "changed");
        Assert.Equal("setup.lock_changed", Assert.Throws<DiskException>(() => DevelopmentEnvironment.ValidateLock(profile, lockFile)).Code);
        File.WriteAllText(At("roms/apple2ee.rom"), "rom");
        File.WriteAllText(At("roms/new.rom"), "rom");
        Assert.Equal("setup.lock_changed", Assert.Throws<DiskException>(() => DevelopmentEnvironment.ValidateLock(profile, lockFile)).Code);
    }

    [Fact]
    public void Lock_CompilerDistribution_FingerprintsLibrariesAndRejectsChanges()
    {
        string profile = WriteProfile(withCompiler: true);
        DevelopmentEnvironmentLock locked = DevelopmentEnvironment.CreateLock(profile, At("lock.json"));
        Assert.Contains(locked.Files, file => file.Path == At("cc65/lib/apple2.lib"));
        File.WriteAllText(At("cc65/cfg/apple2.cfg"), "new config");
        Assert.Throws<DiskException>(() => DevelopmentEnvironment.ValidateLock(profile, At("lock.json")));
    }

    [Fact]
    public void Lock_ExistingDestination_PreservesOriginal()
    {
        string profile = WriteProfile();
        File.WriteAllText(At("lock.json"), "keep");
        Assert.Throws<DiskException>(() => DevelopmentEnvironment.CreateLock(profile, At("lock.json")));
        Assert.Equal("keep", File.ReadAllText(At("lock.json")));
    }

    [Fact]
    public void Lock_InsideFingerprintedDirectory_RejectsSelfReferentialFile()
        => Assert.Equal("setup.lock_location", Assert.Throws<DiskException>(() =>
            DevelopmentEnvironment.CreateLock(WriteProfile(), At("roms/lock.json"))).Code);

    [Fact]
    public void Lock_SymbolicLink_DoesNotTraverseExternalInput()
    {
        if (OperatingSystem.IsWindows()) return;
        string profile = WriteProfile();
        File.CreateSymbolicLink(At("roms/link.rom"), At("mame"));
        Assert.Equal("setup.link", Assert.Throws<DiskException>(() => DevelopmentEnvironment.CreateLock(profile, At("lock.json"))).Code);
    }

    [Fact]
    public void Apply_ProfileDefaults_ResolvesPathsAndPreservesExplicitValues()
    {
        string profile = WriteProfile();
        ExecutionSpec defaults = DevelopmentEnvironment.Apply(new ExecutionSpec(), profile);
        Assert.Equal(At("mame"), defaults.EmulatorPath);
        Assert.Equal(At("roms"), defaults.RomDirectory);
        ExecutionSpec explicitSettings = DevelopmentEnvironment.Apply(new ExecutionSpec { EmulatorPath = "other", Machine = "apple2e" }, profile);
        Assert.Equal("other", explicitSettings.EmulatorPath);
        Assert.Equal("apple2e", explicitSettings.Machine);
        Assert.Throws<DiskException>(() => DevelopmentEnvironment.Apply(explicitSettings with { ToolchainLock = "lock.json" }, profile));
    }

    [Fact]
    public async Task Check_MissingTools_ReportsStructuredActionableFailures()
    {
        EnvironmentCheckResult result = await DevelopmentEnvironment.CheckAsync(new() { MamePath = At("missing"), RomDirectory = At("no-roms") });
        Assert.False(result.Ready);
        Assert.Contains(result.Checks, check => check.Code == "mame.path" && !check.Passed);
        Assert.Contains(result.Checks, check => check.Code == "roms.path" && !check.Passed);
    }

    [Fact]
    public async Task Run_ProgrammaticLockedToolOverride_RejectsBeforeCreatingArtifacts()
    {
        string profile = WriteProfile();
        string lockFile = At("environment.lock.json");
        DevelopmentEnvironment.CreateLock(profile, lockFile);
        File.WriteAllText(At("different-mame"), "other executable");
        File.WriteAllText(At("input.dsk"), "input");
        ExecutionSpec spec = new()
        {
            Environment = profile,
            ToolchainLock = lockFile,
            EmulatorPath = At("different-mame"),
            RomDirectory = At("roms"),
            Machine = "apple2ee",
            DiskImage = At("input.dsk")
        };
        DiskException error = await Assert.ThrowsAsync<DiskException>(() => ExecutionRunner.RunAsync(spec, At("run")));
        Assert.Equal("setup.lock_scope", error.Code);
        Assert.False(Directory.Exists(At("run")));
    }

    [Fact]
    public void Lock_ProfileChanges_InvalidateBeforeDependentPathsAreRead()
    {
        string profile = WriteProfile();
        DevelopmentEnvironment.CreateLock(profile, At("lock.json"));
        DevelopmentEnvironmentProfile changed = DevelopmentEnvironmentProfile.Load(profile) with { RomDirectory = At("missing-roms") };
        File.WriteAllText(profile, JsonSerializer.Serialize(changed, DevelopmentEnvironment.JsonOptions));
        Assert.Equal("setup.lock_changed", Assert.Throws<DiskException>(() => DevelopmentEnvironment.ValidateLock(profile, At("lock.json"))).Code);
    }

    [Theory]
    [InlineData("asm")]
    [InlineData("basic")]
    public void Init_ConfiguredTemplate_BuildsDeterministicallyAndKeepsTemplate(string language)
    {
        string profile = WriteProfile();
        DiskSession.Create(At("template.po"), "prodos");
        DevelopmentEnvironmentProfile settings = DevelopmentEnvironmentProfile.Load(profile) with { TemplateImage = At("template.po") };
        File.WriteAllText(profile, JsonSerializer.Serialize(settings, DevelopmentEnvironment.JsonOptions));
        byte[] original = File.ReadAllBytes(At("template.po"));
        ProjectStarter.Create(At("project"), language, profile);
        string manifest = At("project/project.a2.json");
        ProjectBuildResult first = ProjectBuilder.Build(manifest, At("first.po"));
        ProjectBuildResult second = ProjectBuilder.Build(manifest, At("second.po"));
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(original, File.ReadAllBytes(At("template.po")));
        ExecutionSpec test = BuildExecution.Bind(ExecutionSpec.Load(At("project/execution.json")), first);
        Assert.Equal("2AA55A", Assert.Single(test.Memory).Hex);
        Assert.Equal(90, test.Until!.Value);
        Assert.Equal(At("mame"), test.EmulatorPath);
    }

    [Fact]
    public void Init_UnconfiguredCStarter_WritesProfileAndRefusesOverwrite()
    {
        ProjectStarterResult result = ProjectStarter.Create(At("c-project"), "c");
        Assert.True(result.NeedsEnvironmentConfiguration);
        Assert.True(File.Exists(At("c-project/environment.json")));
        string source = File.ReadAllText(At("c-project/main.c"));
        Assert.Throws<DiskException>(() => ProjectStarter.Create(At("c-project"), "basic"));
        Assert.Equal(source, File.ReadAllText(At("c-project/main.c")));
    }

    [Fact]
    public void Init_BareMetalStarter_BuildsWithoutTemplateAndPinsBootSector()
    {
        string profile = WriteProfile();
        ProjectStarterResult result = ProjectStarter.Create(At("bare-project"), "asm", profile, bareMetal: true);

        ProjectBuildResult build = ProjectBuilder.Build(At("bare-project/project.a2.json"), At("bare.do"));
        Assert.False(result.NeedsEnvironmentConfiguration);
        Assert.Equal("self-booting-unverified", build.Bootability);
        Assert.NotNull(build.Boot);
        Assert.Empty(build.Files);
        using DiskSession disk = DiskSession.Open(At("bare.do"), inputFs: "dos33");
        Assert.Equal(1, disk.ReadBootSectors(1)[0]);
    }

    [Fact]
    public void Init_BareMetalBasic_RejectsBeforeCreatingDirectory()
    {
        Assert.Equal("setup.bare_metal_language", Assert.Throws<DiskException>(() =>
            ProjectStarter.Create(At("bare-basic"), "basic", bareMetal: true)).Code);
        Assert.False(Directory.Exists(At("bare-basic")));
    }

    [Fact]
    public void Build_ChangedLockedEnvironment_FailsBeforeReplacingOutput()
    {
        string profile = WriteProfile();
        string lockFile = At("environment.lock.json");
        DevelopmentEnvironment.CreateLock(profile, lockFile);
        File.WriteAllText(At("main.asm"), ".org $2000\nRTS\n");
        ProjectManifest project = new()
        {
            Environment = profile,
            ToolchainLock = lockFile,
            Files = [new() { Source = "main.asm", Path = "MAIN", Kind = "asm" }]
        };
        File.WriteAllText(At("project.json"), JsonSerializer.Serialize(project, ProjectJson.Options));
        File.WriteAllText(At("output.po"), "preserve");
        File.WriteAllText(At("roms/apple2ee.rom"), "changed");
        Assert.Throws<DiskException>(() => ProjectBuilder.Build(At("project.json"), At("output.po"), overwrite: true));
        Assert.Equal("preserve", File.ReadAllText(At("output.po")));
    }

    private string WriteProfile(bool withCompiler = false)
    {
        File.WriteAllText(At("mame"), "executable");
        Directory.CreateDirectory(At("roms"));
        File.WriteAllText(At("roms/apple2ee.rom"), "rom");
        if (withCompiler)
        {
            foreach (string part in new[] { "bin", "include", "asminc", "lib", "cfg" }) Directory.CreateDirectory(At("cc65/" + part));
            File.WriteAllText(At("cc65/bin/cl65"), "compiler");
            File.WriteAllText(At("cc65/lib/apple2.lib"), "library");
            File.WriteAllText(At("cc65/cfg/apple2.cfg"), "config");
        }
        DevelopmentEnvironmentProfile profile = new()
        {
            MamePath = "mame",
            RomDirectory = "roms",
            Cc65Path = withCompiler ? "cc65/bin/cl65" : null,
            Cc65Root = withCompiler ? "cc65" : null
        };
        File.WriteAllText(At("environment.json"), JsonSerializer.Serialize(profile, DevelopmentEnvironment.JsonOptions));
        return At("environment.json");
    }

    private string At(string path) => Path.GetFullPath(Path.Combine(_directory, path));
    public void Dispose() => Directory.Delete(_directory, true);
}
