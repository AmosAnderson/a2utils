using System.CommandLine;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Projects;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddDevelopmentCommands(RootCommand root)
    {
        Command build = new("build", "Compile a project manifest and commit one validated disk image.");
        Argument<string> project = new("PROJECT");
        Option<string?> output = new("--to") { Description = "Override the manifest output path (relative to the current directory)." };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace the existing output after the complete build validates." };
        Option<bool> check = new("--check") { Description = "Compile and check sources and memory without writing the output image." };
        Option<bool> preflight = new("--preflight") { Description = "Build and validate a disposable image; report changes and capacity without committing output." };
        Option<bool> test = new("--test") { Description = "Build and run the manifest's execution suite against the exact output image." };
        Option<string?> artifacts = new("--artifacts") { Description = "New evidence directory required with --test." };
        build.Arguments.Add(project);
        build.Options.Add(output); build.Options.Add(overwrite); build.Options.Add(check);
        build.Options.Add(preflight); build.Options.Add(test); build.Options.Add(artifacts);
        build.SetAction(parse =>
        {
            if ((parse.GetValue(check) ? 1 : 0) + (parse.GetValue(preflight) ? 1 : 0) + (parse.GetValue(test) ? 1 : 0) > 1)
                throw new DiskException("project.mode", "Choose only one of --check, --preflight, or --test.", 2);
            if (parse.GetValue(test) != !string.IsNullOrWhiteSpace(parse.GetValue(artifacts)))
                throw new DiskException("project.artifacts", "--test requires --artifacts; --artifacts is only used with --test.", 2);
            string? destination = parse.GetValue(output) is { } path ? Path.GetFullPath(path) : null;
            if (parse.GetValue(test))
            {
                ProjectWorkflowResult workflow = ProjectWorkflow.Run(parse.GetValue(project)!, parse.GetValue(artifacts)!,
                    destination, parse.GetValue(overwrite), _cancellationToken);
                EmitProgramDiagnostics(workflow.Build.Diagnostics);
                Result("build.test", workflow, $"Built {workflow.Build.OutputPath}; {workflow.Tests.Tests.Count(run => run.Passed)}/{workflow.Tests.Tests.Count} tests passed.\nArtifacts: {workflow.ArtifactDirectory}");
                return workflow.Cancelled ? 6 : workflow.Passed ? 0 : 1;
            }
            ProjectBuildResult result = ProjectBuilder.Build(parse.GetValue(project)!, destination,
                parse.GetValue(overwrite), parse.GetValue(check), _cancellationToken, preflight: parse.GetValue(preflight));
            EmitProgramDiagnostics(result.Diagnostics);
            return Result("build", result, parse.GetValue(check) ? $"Checked {result.Files.Count} files for {result.Target}."
                : parse.GetValue(preflight) ? $"Preflight passed: {result.Files.Count} files; {result.Plan!.FreeBytesAfter} bytes free after planned changes."
                : $"Built {result.OutputPath} ({result.Files.Count} files, {result.Bootability}).");
        });
        root.Subcommands.Add(build);

        Command targets = new("targets", "Describe machine profiles, runtime memory reservations, and platform symbols.");
        targets.SetAction(_ => Result("targets", new
        {
            profiles = TargetProfiles.All,
            symbols = TargetProfiles.Symbols,
            runtimeReservations = new { dos33 = TargetProfiles.Reserved("dos33"), prodos = TargetProfiles.Reserved("prodos") }
        }, string.Join(Environment.NewLine, TargetProfiles.All.Select(profile => $"{profile.Name}: {profile.Description} ({profile.Cpu})"))));
        root.Subcommands.Add(targets);

        Command capabilities = new("capabilities", "Discover commands, arguments, options, supported formats, and schemas.");
        capabilities.SetAction(_ => Result("capabilities", new
        {
            schemaVersion = 1,
            commands = DescribeCommands(root, "a2"),
            globalOptions = root.Options.Select(option => new { option.Name, option.Description, aliases = option.Aliases.ToArray() }),
            cpus = new[] { "6502", "65c02", "w65c02" },
            targets = TargetProfiles.All.Select(profile => profile.Name),
            filesystems = new[] { "dos33", "prodos" },
            containers = new[] { "raw", "2mg" },
            sourceKinds = new[] { "asm", "basic", "basic-labels", "binary", "text", "applesingle", "cc65", "lores", "hires", "hires-color" },
            graphicsModes = new[] { "lores", "hires", "hires-color" },
            graphicsAssets = new
            {
                kinds = new[] { "sprite", "tile", "font", "shape-table" },
                doubleHiresModes = new[] { "mono", "color" },
                bankOrders = new[] { "aux-main", "main-aux" }
            },
            schemas = new[] { "project", "diagnostic", "execution", "execution-suite", "environment" },
            externalTools = new[] { "MAME 0.289 for run/test; matching ROMs and a disk or direct routine", "Optional cc65 cl65 for C/ca65 builds" },
            developmentWorkflow = new
            {
                fullPreflight = true,
                projectTests = true,
                symbolicAssertions = true,
                environmentLocks = true,
                projectAssetPipeline = true,
                runtimeMemoryAccounting = true,
                overlayGroups = true,
                compilerDiagnostics = true,
                diskDevices = new[] { "flop1", "flop2", "hard1", "hard2" },
                storageProfiles = new[] { "floppy", "cffa2" },
                stepActions = new[] { "wait", "assert", "keys", "delay", "input", "capture" },
                gamePorts = new[] { "none", "joystick", "paddles" },
                routineHarness = true,
                cycleBudgets = true,
                audioAssertions = true,
                visualAssertions = true,
                postRunDiskAssertions = true,
                breakpoints = true,
                watchpoints = true,
                boundedInstructionSteps = true,
                instructionAddressHistory = true,
                memoryBanks = new[] { "cpu", "main", "aux", "lc1", "lc2", "aux-lc1", "aux-lc2" },
                textColumns = new[] { 40, 80 },
                mouseTextCells = true
            },
            limitations = new[] { "New formatted disks are data volumes; bootable builds use a supplied template.",
                "BASIC checks cover common source errors, not all runtime behavior.",
                "Banked project files require an explicit loader; runtime bank switching is application-owned.",
                "Debug history disassembles retained PCs using capture-time memory; it does not preserve historical banks or bytes.",
                "Native IIgs development is not supported." }
        }, "Use --json for the complete command and capability catalog."));
        root.Subcommands.Add(capabilities);

        Command schema = new("schema", "Return a bundled development JSON Schema as a versioned result.");
        Argument<string> name = new("NAME");
        schema.Arguments.Add(name);
        schema.SetAction(parse =>
        {
            string requested = parse.GetValue(name)!;
            if (requested is not ("project" or "diagnostic" or "execution" or "execution-suite" or "environment"))
                throw new DiskException("schema.unknown", "Schema must be project, diagnostic, execution, execution-suite, or environment.", 2);
            string resource = typeof(CliApplication).Assembly.GetManifestResourceNames()
                .Single(item => item.EndsWith($".{requested}.schema.json", StringComparison.Ordinal));
            using Stream stream = typeof(CliApplication).Assembly.GetManifestResourceStream(resource)!;
            using JsonDocument document = JsonDocument.Parse(stream);
            return Result("schema", document.RootElement.Clone(), document.RootElement.GetRawText());
        });
        root.Subcommands.Add(schema);
    }

    private static object[] DescribeCommands(Command command, string prefix)
        => command.Subcommands.Select(child => (object)new
        {
            name = prefix + " " + child.Name,
            child.Description,
            aliases = child.Aliases.ToArray(),
            arguments = child.Arguments.Select(argument => new
            {
                argument.Name,
                argument.Description,
                minimum = argument.Arity.MinimumNumberOfValues,
                maximum = argument.Arity.MaximumNumberOfValues
            }).ToArray(),
            options = child.Options.Concat(command is RootCommand ? command.Options : [])
                .Select(option => new { option.Name, option.Description, option.Required, aliases = option.Aliases.ToArray() }).ToArray(),
            subcommands = DescribeCommands(child, prefix + " " + child.Name)
        }).ToArray();
}
