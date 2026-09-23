// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Execution;
using A2Utils.Core.Projects;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddDevelopmentCommands(RootCommand root)
    {
        Command build = new("build", "Compile, check, preflight, or build a project manifest.");
        Argument<string> project = new("PROJECT");
        Option<string?> output = new("--to") { Description = "Override the manifest output path (relative to the current directory)." };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace the existing output after the complete build validates." };
        Option<bool> check = new("--check") { Description = "Compile and check sources and memory without writing the output image." };
        Option<bool> preflight = new("--preflight") { Description = "Build and validate a disposable image; report changes and capacity without committing output." };
        Option<bool> test = new("--test") { Description = "Build and run the manifest's build-bound execution suite." };
        Option<string?> cache = new("--cache") { Description = "Use DIRECTORY as an opt-in content-addressed cache for normal builds." };
        Option<string?> artifacts = new("--artifacts") { Description = "Evidence directory required with --test; must be new unless --run-subdirectory treats it as an existing parent." };
        Option<string[]> testFilters = new("--filter")
        {
            Description = "With --test, select suite cases by name/path glob; may be repeated.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        Option<int?> testJobs = new("--jobs") { Description = "With --test, run 1-16 selected cases concurrently; default 1." };
        Option<string?> testRerunFailed = new("--rerun-failed") { Description = "With --test, select failures from a previous suite result or run directory." };
        Option<bool> testProgress = new("--progress") { Description = "With --test, write flushed JSONL events to the evidence directory." };
        Option<bool> testRunSubdirectory = new("--run-subdirectory") { Description = "With --test, create an immutable run-* child beneath --artifacts." };
        build.Arguments.Add(project);
        build.Options.Add(output); build.Options.Add(overwrite); build.Options.Add(check);
        build.Options.Add(preflight); build.Options.Add(test); build.Options.Add(cache); build.Options.Add(artifacts);
        build.Options.Add(testFilters); build.Options.Add(testJobs); build.Options.Add(testRerunFailed);
        build.Options.Add(testProgress); build.Options.Add(testRunSubdirectory);
        build.SetAction(parse =>
        {
            if ((parse.GetValue(check) ? 1 : 0) + (parse.GetValue(preflight) ? 1 : 0) + (parse.GetValue(test) ? 1 : 0) > 1)
                throw new DiskException("project.mode", "Choose only one of --check, --preflight, or --test.", 2);
            string? cacheDirectory = parse.GetValue(cache);
            if (cacheDirectory is not null && string.IsNullOrWhiteSpace(cacheDirectory))
                throw new DiskException("project.cache", "--cache requires a non-empty directory.", 2);
            if (cacheDirectory is not null && (parse.GetValue(check) || parse.GetValue(preflight) || parse.GetValue(test)))
                throw new DiskException("project.cache", "--cache is available only for normal builds; do not combine it with --check, --preflight, or --test.", 2);
            if (parse.GetValue(test) != !string.IsNullOrWhiteSpace(parse.GetValue(artifacts)))
                throw new DiskException("project.artifacts", "--test requires --artifacts; --artifacts is only used with --test.", 2);
            bool hasSuiteOptions = parse.GetValue(testFilters)?.Length > 0 || parse.GetValue(testJobs) is not null
                || parse.GetValue(testRerunFailed) is not null || parse.GetValue(testProgress) || parse.GetValue(testRunSubdirectory);
            if (!parse.GetValue(test) && hasSuiteOptions)
                throw new DiskException("project.test_options", "--filter, --jobs, --rerun-failed, --progress, and --run-subdirectory require --test.", 2);
            string? destination = parse.GetValue(output) is { } path ? Path.GetFullPath(path) : null;
            if (parse.GetValue(test))
            {
                ExecutionSuiteRunOptions suiteOptions = CreateSuiteOptions(parse.GetValue(testFilters), parse.GetValue(testJobs),
                    parse.GetValue(testRerunFailed), parse.GetValue(testProgress), parse.GetValue(testRunSubdirectory));
                ProjectWorkflowResult workflow = ProjectWorkflow.Run(parse.GetValue(project)!, parse.GetValue(artifacts)!,
                    destination, parse.GetValue(overwrite), suiteOptions, _cancellationToken);
                EmitProgramDiagnostics(workflow.Build.Diagnostics);
                Result("build.test", workflow, $"Built {workflow.Build.OutputPath}; {workflow.Tests.Counts.Passed}/{workflow.Tests.Counts.Planned} tests passed.\nArtifacts: {workflow.ArtifactDirectory}");
                return workflow.Cancelled ? 6 : workflow.Passed ? 0 : 1;
            }
            ProjectBuildResult result = cacheDirectory is null
                ? ProjectBuilder.Build(parse.GetValue(project)!, destination,
                    parse.GetValue(overwrite), parse.GetValue(check), _cancellationToken,
                    preflight: parse.GetValue(preflight))
                : ProjectBuildCache.Build(parse.GetValue(project)!, Path.GetFullPath(cacheDirectory),
                    destination, parse.GetValue(overwrite), _cancellationToken);
            EmitProgramDiagnostics(result.Diagnostics);
            return Result("build", result, parse.GetValue(check) ? $"Checked {result.Files.Count} files for {result.Target}."
                : parse.GetValue(preflight) ? $"Preflight passed: {result.Files.Count} files; {result.Plan!.FreeBytesAfter} bytes free after planned changes."
                : $"Built {result.OutputPath} ({result.Files.Count} files, {result.Bootability}; cache {(cacheDirectory is null ? "disabled" : result.CacheHit ? "hit" : "miss")}).");
        });
        root.Subcommands.Add(build);

        Command projectCommands = new("project", "Resolve manifests or adopt existing disk images as projects.");
        Command resolve = new("resolve", "Compile checks and return effective settings, dependencies, tools, memory, and disk plan without committing output.");
        resolve.Aliases.Add("inspect");
        Argument<string> manifest = new("PROJECT") { Description = "Project manifest to resolve." };
        resolve.Arguments.Add(manifest);
        resolve.SetAction(parse =>
        {
            ProjectResolutionResult result = ProjectResolver.Inspect(parse.GetValue(manifest)!,
                _cancellationToken);
            EmitProgramDiagnostics(result.Diagnostics);
            return Result("project.resolve", result,
                $"Resolved {result.Files.Count} files for {result.Settings.Target}; output would be {result.OutputPath}.");
        });
        projectCommands.Subcommands.Add(resolve);

        Command importProject = new("import", "Adopt an existing disk image as a hash-pinned editable project in a new directory.");
        Argument<string> importImage = new("IMAGE") { Description = "Existing supported disk image." };
        Option<string> importDestination = new("--to") { Required = true, Description = "New project directory." };
        Option<bool> disassemble = new("--disassemble") { Description = "Convert load-addressed BIN files to reassemblable source." };
        Option<string> importTarget = new("--target") { DefaultValueFactory = _ => "apple2e", Description = "apple2plus, apple2e, apple2enh, or apple2c." };
        importProject.Arguments.Add(importImage);
        importProject.Options.Add(importDestination);
        importProject.Options.Add(disassemble);
        importProject.Options.Add(importTarget);
        importProject.SetAction(parse =>
        {
            ProjectImportResult result = ProjectImporter.Import(parse.GetValue(importImage)!,
                parse.GetValue(importDestination)!, parse.GetValue(disassemble),
                parse.GetValue(importTarget)!, parse.GetValue(_inputOrder),
                parse.GetValue(_inputFs), _cancellationToken);
            return Result("project.import", result,
                $"Imported {result.Files.Count} files into {result.Directory}.");
        });
        projectCommands.Subcommands.Add(importProject);
        root.Subcommands.Add(projectCommands);

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
            globalOptions = root.Options.Select(option => DescribeOption("a2", option)),
            automationContract = new
            {
                contractVersion = 2,
                envelopeSchemaId = CliContractSchemas.Envelope,
                resultSchemaId = CliContractSchemas.Result,
                errorSchemaId = CliContractSchemas.Error
            },
            cpus = new[] { "6502", "65c02", "w65c02" },
            executionEngines = new[] { "mame", "cpu" },
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
            schemas = SchemaNames,
            externalTools = new object[]
            {
                new { id = "mame", requiredFor = new[] { "execution specs/cases with engine mame" }, expectedVersion = "0.289", optional = true },
                new { id = "cc65", requiredFor = new[] { "a2 cc compile", "project files with kind cc65" }, expectedVersion = (string?)null, optional = true }
            },
            agentInterfaces = new
            {
                mcp = new
                {
                    command = "a2 mcp serve",
                    transport = "stdio",
                    tools = new[] { "a2_cli", "a2_capabilities", "a2_schema" }
                }
            },
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
                contentAddressedBuildCache = true,
                bareMetalProjects = new
                {
                    starter = true,
                    starterLanguages = new[] { "asm" },
                    bootSourceKinds = new[] { "asm", "binary" },
                    origin = 0x0800,
                    maximumBootSectors = 16
                },
                selectiveSuites = new
                {
                    list = true,
                    filters = new[] { "name glob", "path glob" },
                    maximumParallelJobs = 16,
                    rerunFailed = true,
                    progressJsonLines = true,
                    immutableRunSubdirectories = true
                },
                cpuRoutineExecution = true,
                cpuInstructionTrace = true,
                cpuProcessors = new[] { "6502", "apple65c02" },
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
                graphicsMemoryAssertions = new
                {
                    modes = new[] { "lores", "hires", "hires-color", "dhires-mono", "dhires-color" },
                    pages = new[] { 1, 2 },
                    banks = new[] { "cpu", "main", "aux" },
                    stepConditions = true
                },
                memoryBanks = new[] { "cpu", "main", "aux", "lc1", "lc2", "aux-lc1", "aux-lc2" },
                textColumns = new[] { 40, 80 },
                mouseTextCells = true
            },
            limitations = new[] { "Ordinary formatted disks are data volumes; a project boot declaration can write original track-zero boot code.",
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
            if (!SchemaNames.Contains(requested, StringComparer.Ordinal))
                throw new DiskException("schema.unknown", "Schema must be one of: " + string.Join(", ", SchemaNames) + ".", 2);
            string resource = typeof(CliApplication).Assembly.GetManifestResourceNames()
                .Single(item => item.EndsWith($".{requested}.schema.json", StringComparison.Ordinal));
            using Stream stream = typeof(CliApplication).Assembly.GetManifestResourceStream(resource)!;
            using JsonDocument document = JsonDocument.Parse(stream);
            return Result("schema", document.RootElement.Clone(), document.RootElement.GetRawText());
        });
        root.Subcommands.Add(schema);
    }

    private static readonly string[] SchemaNames =
    [
        "project", "project-resolution", "diagnostic", "execution", "execution-suite",
        "environment", "disk-change-set", "result", "error", "envelope"
    ];

    private static CliCommandCapability[] DescribeCommands(Command command, string prefix,
        IReadOnlyList<Option>? inheritedGlobals = null)
    {
        inheritedGlobals ??= command is RootCommand
            ? command.Options.Where(option => option.Recursive).ToArray() : [];
        return command.Subcommands.Select(child =>
        {
            string name = prefix + " " + child.Name;
            return new CliCommandCapability(name, child.Description, child.Aliases.ToArray(),
                child.Arguments.Select(argument => DescribeArgument(name, argument)).ToArray(),
                child.Options.Concat(inheritedGlobals)
                    .DistinctBy(option => option.Name, StringComparer.Ordinal)
                    .Select(option => DescribeOption(name, option)).ToArray(),
                DescribeCommands(child, name, inheritedGlobals), SideEffects(name),
                name == "a2 project resolve" ? CliContractSchemas.ProjectResolution : CliContractSchemas.Result,
                CliContractSchemas.Error);
        }).ToArray();
    }

    private static CliArgumentCapability DescribeArgument(string command, Argument argument)
        => new(argument.Name, argument.Description, ValueType(argument.ValueType),
            argument.Arity.MinimumNumberOfValues, argument.Arity.MaximumNumberOfValues,
            ArgumentPathRole(command, argument.Name));

    private static CliOptionCapability DescribeOption(string command, Option option)
    {
        bool isBoolean = option.ValueType == typeof(bool) || option.Name is "--help" or "--version";
        bool hasDefault = option.HasDefaultValue || isBoolean;
        object? defaultValue = option.HasDefaultValue ? option.GetDefaultValue()
            : isBoolean ? false : EffectiveDefault(command, option.Name);
        hasDefault |= defaultValue is not null;
        (string[] conflicts, string[] requires) = OptionRelationships(command, option.Name);
        return new(option.Name, option.Description, isBoolean ? "boolean" : ValueType(option.ValueType), option.Required,
            option.Aliases.ToArray(), hasDefault, defaultValue, OptionChoices(command, option.Name),
            conflicts, requires, OptionPathRole(command, option.Name));
    }

    private static object? EffectiveDefault(string command, string option) => (command, option) switch
    {
        ("a2 disk create", "--size") => "140k",
        ("a2 disk add", "--format") => "binary",
        ("a2 asm compile" or "a2 asm decompile" or "a2 basic compile" or "a2 basic decompile", "--format") => "raw",
        ("a2 test" or "a2 build", "--jobs") => 1,
        _ => null
    };

    private static string ValueType(Type type)
    {
        Type value = Nullable.GetUnderlyingType(type) ?? type;
        if (value == typeof(bool)) return "boolean";
        if (value == typeof(byte) || value == typeof(short) || value == typeof(int) || value == typeof(long)
            || value == typeof(ushort) || value == typeof(uint) || value == typeof(ulong)) return "integer";
        if (value == typeof(float) || value == typeof(double) || value == typeof(decimal)) return "number";
        if (value != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(value)) return "array";
        return "string";
    }

    private static object[] OptionChoices(string command, string option)
    {
        string[] values = (command, option) switch
        {
            (_, "--input-order") or (_, "--order") or (_, "--source-order") or (_, "--after-input-order") => ["dos", "prodos"],
            (_, "--input-fs") or (_, "--source-fs") or (_, "--after-input-fs") or ("a2 disk create", "--fs") => ["dos33", "prodos"],
            (_, "--container") => ["raw", "2mg"],
            ("a2 build", _) when option is "--check" or "--preflight" or "--test" => [],
            ("a2 init", "--language") => ["asm", "basic", "c"],
            ("a2 project import", "--target") => ["apple2plus", "apple2e", "apple2enh", "apple2c"],
            ("a2 cc compile", "--target") => ["apple2", "apple2enh"],
            ("a2 asm compile" or "a2 asm decompile" or "a2 asm listing" or "a2 asm map", "--cpu") => ["6502", "65c02", "w65c02"],
            ("a2 asm compile" or "a2 asm decompile" or "a2 basic compile" or "a2 basic decompile", "--format") => ["raw", "dos"],
            ("a2 disk export", "--format") => ["binary", "text"],
            ("a2 disk add" or "a2 disk replace" or "a2 disk import", "--format") => ["binary", "text"],
            ("a2 graphics encode" or "a2 graphics decode", "--mode") => ["lores", "hires", "hires-color"],
            ("a2 graphics assets pack" or "a2 graphics assets unpack", "--bit-order") => ["lsb", "msb"],
            ("a2 graphics assets pack" or "a2 graphics assets unpack", "--kind") => ["sprite", "tile", "font"],
            ("a2 graphics dhires encode" or "a2 graphics dhires decode", "--mode") => ["mono", "color"],
            ("a2 graphics dhires encode" or "a2 graphics dhires decode", "--bank-order") => ["aux-main", "main-aux"],
            _ => []
        };
        return values.Cast<object>().ToArray();
    }

    private static (string[] Conflicts, string[] Requires) OptionRelationships(string command,
        string option) => (command, option) switch
        {
            ("a2 build", "--check") => (["--preflight", "--test", "--cache"], []),
            ("a2 build", "--preflight") => (["--check", "--test", "--cache"], []),
            ("a2 build", "--test") => (["--check", "--preflight", "--cache"], ["--artifacts"]),
            ("a2 build", "--cache") => (["--check", "--preflight", "--test"], []),
            ("a2 build", "--artifacts") => ([], ["--test"]),
            ("a2 build", "--filter" or "--jobs" or "--rerun-failed" or "--progress" or "--run-subdirectory")
                => ([], ["--test"]),
            ("a2 test", "--list") => (["--progress", "--run-subdirectory"], []),
            ("a2 test", "--progress" or "--run-subdirectory") => (["--list"], ["--artifacts"]),
            ("a2 disk create", "--size") => (["--blocks"], []),
            ("a2 disk create", "--blocks") => (["--size"], []),
            ("a2 disk attr", "--lock") => (["--unlock"], []),
            ("a2 disk attr", "--unlock") => (["--lock"], []),
            ("a2 disk add" or "a2 disk replace" or "a2 disk delete" or "a2 disk rename"
                or "a2 disk mkdir" or "a2 disk attr" or "a2 disk copy" or "a2 disk move"
                or "a2 disk import" or "a2 disk apply", "--output") => (["--in-place"], []),
            ("a2 disk add" or "a2 disk replace" or "a2 disk delete" or "a2 disk rename"
                or "a2 disk mkdir" or "a2 disk attr" or "a2 disk copy" or "a2 disk move"
                or "a2 disk import" or "a2 disk apply", "--in-place") => (["--output"], []),
            ("a2 asm decompile" or "a2 basic decompile", "--from-image") => (["--format"], []),
            ("a2 asm decompile" or "a2 basic decompile", "--format") => (["--from-image"], []),
            _ => ([], [])
        };

    private static string? ArgumentPathRole(string command, string argument) => argument switch
    {
        "PROJECT" or "SPEC" or "SUITE" or "PROFILE" or "BEFORE" or "AFTER" or "CHANGES" => "input-file",
        "IMAGE" => "input-file",
        "OUTPUT" => "output-file",
        "HOSTFILE" => "input-file",
        "HOSTDIRECTORY" => "input-directory",
        "DIRECTORY" when command == "a2 init" => "output-directory",
        "INPUT" when command is "a2 asm decompile" or "a2 basic decompile" => "input-file-or-image-entry",
        "INPUT" => "input-file",
        "PATH" or "SOURCE" or "DESTINATION" or "NEWNAME" => "image-entry",
        _ => null
    };

    private static string? OptionPathRole(string command, string option) => option switch
    {
        "--artifacts" => "output-directory",
        "--cache" => "cache-directory",
        "--manifest" or "--from" or "--from-image" or "--environment" => "input-file",
        "--rerun-failed" => "input-file-or-directory",
        "--project-root" => "input-directory",
        "--compiler" => "executable",
        "--output" => "output-file",
        "--to" when command is "a2 disk extract" or "a2 project import" => "output-directory",
        "--to" when command == "a2 disk import" => "image-entry",
        "--to" => "output-file",
        _ => null
    };

    private static string[] SideEffects(string command)
    {
        if (command is "a2 capabilities" or "a2 targets" or "a2 schema")
            return [];
        if (command == "a2 project resolve")
            return ["reads-host-files", "may-run-external-process", "uses-temporary-storage"];
        if (command == "a2 project import") return ["reads-disk-image", "creates-host-directory"];
        if (command == "a2 mcp serve") return ["stdio-server", "delegates-to-invoked-command"];
        if (command is "a2 run" or "a2 test")
            return ["may-run-external-process", "may-create-artifacts"];
        if (command == "a2 build") return ["may-run-external-process", "may-write-disk-image", "may-create-artifacts"];
        if (command == "a2 env check") return ["reads-host-files", "may-run-external-process"];
        if (command == "a2 env lock") return ["reads-host-files", "creates-host-file"];
        if (command == "a2 init") return ["creates-host-directory"];
        if (command == "a2 basic check") return ["reads-host-files"];
        if (command is "a2 graphics shapes" or "a2 graphics dhires" or "a2 graphics assets") return [];
        if (command.StartsWith("a2 disk ", StringComparison.Ordinal))
        {
            string leaf = command[8..];
            if (leaf is "info" or "ls" or "verify") return ["reads-disk-image"];
            if (leaf == "extract") return ["reads-disk-image", "creates-host-directory"];
            if (leaf == "diff") return ["reads-disk-images", "uses-temporary-storage"];
            if (leaf == "plan") return ["reads-disk-image", "reads-host-files", "uses-temporary-storage"];
            if (leaf == "apply") return ["reads-disk-image", "reads-host-files", "writes-host-file-or-disk-image", "uses-temporary-storage"];
            if (leaf == "create") return ["writes-host-file-or-disk-image"];
            if (leaf == "export") return ["reads-disk-image", "writes-host-file"];
            if (leaf is "add" or "replace" or "import")
                return ["reads-disk-image", "reads-host-files", "writes-host-file-or-disk-image"];
            if (leaf == "copy") return ["reads-disk-images", "writes-host-file-or-disk-image"];
            return ["reads-disk-image", "writes-host-file-or-disk-image"];
        }
        if (command == "a2 cc compile") return ["runs-external-process", "writes-host-file"];
        if (command.IndexOf(' ', 3) < 0 || command.Split(' ').Length == 2) return [];
        return ["reads-host-files", "writes-host-file"];
    }
}
