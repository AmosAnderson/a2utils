// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Backends;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Cli;

/// <summary>The CLI boundary; output is injectable for tests and embedding.</summary>
public sealed partial class CliApplication
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly CancellationToken _cancellationToken;
    private readonly Option<bool> _json = new("--json") { Description = "Emit versioned JSON.", Recursive = true };
    private readonly Option<bool> _quiet = new("--quiet") { Description = "Suppress normal text output.", Recursive = true };
    private readonly Option<bool> _verbose = new("--verbose") { Description = "Include diagnostic detail on stderr.", Recursive = true };
    private readonly Option<string?> _inputOrder = new("--input-order") { Description = "Input layout override: dos or prodos.", Recursive = true };
    private readonly Option<string?> _inputFs = new("--input-fs") { Description = "Filesystem override: dos33 or prodos.", Recursive = true };
    private ParseResult? _parse;
    private readonly List<ProgramDiagnostic> _pendingDiagnostics = [];
    private readonly HashSet<ProgramDiagnostic> _renderedProgramDiagnostics = [];
    private static ParserConfiguration ParserConfiguration { get; } = new()
    {
        ResponseFileTokenReplacer = null
    };

    private CliApplication(TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        _output = output;
        _error = error;
        _cancellationToken = cancellationToken;
    }

    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
        => new CliApplication(output ?? Console.Out, error ?? Console.Error, cancellationToken).Invoke(args);

    private int Invoke(string[] args)
    {
        try
        {
            RootCommand root = BuildCommands();
            _parse = root.Parse(args, ParserConfiguration);
            if (_parse.Errors.Count > 0)
            {
                return Fail("invalid_arguments", string.Join(Environment.NewLine,
                    _parse.Errors.Select(e => e.Message)), 2);
            }
            ValidateChoice(_parse.GetValue(_inputOrder), "--input-order", "dos", "prodos");
            ValidateChoice(_parse.GetValue(_inputFs), "--input-fs", "dos33", "prodos");
            _cancellationToken.ThrowIfCancellationRequested();
            return _parse.Invoke(new InvocationConfiguration
            {
                Output = _output,
                Error = _error,
                EnableDefaultExceptionHandler = false
            });
        }
        catch (DiskException ex)
        {
            return Fail(ex.Code, ex.Message, ex.ExitCode, ex.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Fail("cancelled", "Operation cancelled before completion.", 6);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("host_io", ex.Message, 5);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return Fail("invalid_arguments", ex.Message, 2);
        }
        catch (Exception ex)
        {
            if (_parse?.GetValue(_verbose) == true && _parse?.GetValue(_json) != true)
            {
                _error.WriteLine(ex);
            }
            return Fail("unexpected_error", ex.Message, 1);
        }
    }

    private RootCommand BuildCommands()
    {
        RootCommand root = new("Apple II disk image and program utilities.");
        Command disk = new("disk", "Inspect, extract, create, and modify DOS 3.3 and ProDOS images.");
        root.Subcommands.Add(disk);
        foreach (Option option in new Option[] { _json, _quiet, _verbose, _inputOrder, _inputFs })
        {
            root.Options.Add(option);
        }

        AddReadCommands(disk);
        AddCreateCommand(disk);
        AddImportCommand(disk);
        AddMutationCommands(disk);
        AddAttributeCommand(disk);
        AddConvertCommand(disk);
        AddDiskPlanningCommands(disk);
        AddTransferCommands(disk);
        AddProgramCommands(root);
        AddDevelopmentCommands(root);
        AddGraphicsCommands(root);
        AddExecutionCommands(root);
        AddCc65Commands(root);
        AddSetupCommands(root);
        AddMcpCommands(root);
        return root;
    }

    private void AddReadCommands(Command disk)
    {
        foreach (string name in new[] { "info", "ls", "verify", "extract" })
        {
            string description = name switch
            {
                "info" => "Describe the image container, layout, filesystem, and diagnostics.",
                "ls" => "List files and directories.",
                "verify" => "Check structural consistency without repairing the image.",
                _ => "Extract stored bytes and a metadata manifest into a new directory."
            };
            Command command = new(name, description);
            Argument<string> image = new("IMAGE");
            command.Arguments.Add(image);
            Argument<string?> path = new("PATH") { Arity = ArgumentArity.ZeroOrOne };
            Option<bool> recursive = new("--recursive") { Description = "Include subdirectory contents." };
            Option<string?> destination = new("--to") { Description = "New host output directory.", Required = true };
            if (name is "ls" or "extract")
            {
                command.Arguments.Add(path);
            }
            if (name == "ls")
            {
                command.Options.Add(recursive);
            }
            if (name == "extract")
            {
                command.Options.Add(destination);
            }
            command.SetAction(parse =>
            {
                if (name == "info")
                {
                    DiskInfo info = DiskSession.Inspect(parse.GetValue(image)!,
                        parse.GetValue(_inputOrder), parse.GetValue(_inputFs));
                    EmitDiagnostics(info.Diagnostics);
                    string free = info.FreeBytes is { } freeBytes ? $"{freeBytes:N0} bytes free" : "free space unknown";
                    return Result(name, info, $"{info.Container} | {info.Order} order | {info.FileSystem}\n"
                        + $"Volume: {info.VolumeName} | {info.SizeBytes:N0} bytes | {free}");
                }
                using DiskSession session = Open(parse.GetValue(image)!);
                EmitDiagnostics(session.Info.Diagnostics);
                switch (name)
                {
                    case "ls":
                        IReadOnlyList<DiskEntry> entries = session.List(parse.GetValue(path), parse.GetValue(recursive));
                        return Result(name, entries, string.Join(Environment.NewLine, entries.Select(e =>
                            $"{(e.IsDirectory ? "DIR" : e.Type),-4} {(e.IsLocked ? '*' : ' ')} {e.Length,10}  {e.Path}")));
                    case "verify":
                        IReadOnlyList<DiskDiagnostic> diagnostics = session.Verify();
                        bool corrupt = session.Info.IsDubious || diagnostics.Any(d =>
                            string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase));
                        Result(name, new { valid = !corrupt, diagnostics }, corrupt
                            ? "Verification found structural errors." : "Verification passed.");
                        return corrupt ? 4 : 0;
                    default:
                        ExtractionManifest manifest = FileTransfer.Extract(session, parse.GetValue(destination)!,
                            parse.GetValue(path), _cancellationToken);
                        return Result(name, new { destination = Path.GetFullPath(parse.GetValue(destination)!), manifest },
                            $"Extracted {manifest.Entries.Count(e => !e.Entry.IsDirectory)} files to {parse.GetValue(destination)}");
                }
            });
            disk.Subcommands.Add(command);
        }
    }

    private void AddCreateCommand(Command disk)
    {
        Command command = new("create", "Create a formatted data volume (boot code is not installed).");
        Argument<string> output = new("OUTPUT");
        Option<string> fs = new("--fs") { Required = true, Description = "dos33 or prodos." };
        Option<string?> size = new("--size") { Description = "Capacity in KiB (140k, 800k) or MiB (16m). Default: 140k." };
        Option<int?> blocks = new("--blocks") { Description = "Exact ProDOS capacity, up to 65535 blocks." };
        Option<string> container = new("--container") { DefaultValueFactory = _ => "raw", Description = "raw or 2mg." };
        Option<string?> order = new("--order") { Description = "dos or prodos; defaults to filesystem order." };
        Option<string> volumeName = new("--volume-name") { DefaultValueFactory = _ => "UNTITLED" };
        Option<int> volumeNumber = new("--volume-number") { DefaultValueFactory = _ => 254 };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace an existing output file after validation." };
        command.Arguments.Add(output);
        foreach (Option option in new Option[] { fs, size, blocks, container, order, volumeName, volumeNumber, overwrite })
        {
            command.Options.Add(option);
        }
        command.SetAction(parse =>
        {
            string fileSystem = parse.GetValue(fs)!;
            string layout = parse.GetValue(order) ?? (fileSystem == "prodos" ? "prodos" : "dos");
            string format = parse.GetValue(container)!;
            ValidateChoice(fileSystem, "--fs", "dos33", "prodos");
            ValidateChoice(layout, "--order", "dos", "prodos");
            ValidateChoice(format, "--container", "raw", "2mg");
            if (parse.GetValue(blocks) is not null && parse.GetValue(size) is not null)
            {
                throw new DiskException("invalid_arguments", "Specify --size or --blocks, not both.", 2);
            }
            ImageWriteResult result = ImageTransactions.Create(parse.GetValue(output)!, parse.GetValue(overwrite),
                temporary => DiskSession.Create(temporary, fileSystem, ParseSize(parse.GetValue(size)),
                    format, layout, parse.GetValue(volumeName)!, parse.GetValue(volumeNumber), parse.GetValue(blocks)),
                temporary => ValidateImage(temporary, layout, fileSystem), _cancellationToken);
            return Result("create", result, $"Created {result.OutputPath}");
        });
        disk.Subcommands.Add(command);
    }

    private void AddImportCommand(Command disk)
    {
        Command command = new("add", "Import a host payload or restore a same-filesystem extraction manifest.");
        Argument<string> image = new("IMAGE");
        Argument<string?> host = new("HOSTFILE") { Arity = ArgumentArity.ZeroOrOne };
        Option<string?> name = new("--name") { Description = "New image filename or ProDOS path." };
        Option<string?> type = new("--type") { Description = "DOS type (B, A, I, T) or ProDOS type (BIN, TXT, hex)." };
        Option<string?> address = new("--load-address") { Description = "DOS binary load address, e.g. 0x2000." };
        Option<string?> auxiliary = new("--aux-type") { Description = "ProDOS auxiliary type, e.g. 0x2000." };
        Option<string?> manifest = new("--manifest") { Description = "Restore stored files and metadata from a2-manifest.json." };
        Option<string?> format = new("--format") { Description = "binary payload (default) or UTF-8 text converted for the target filesystem." };
        command.Arguments.Add(image);
        command.Arguments.Add(host);
        foreach (Option option in new Option[] { name, type, address, auxiliary, manifest, format })
        {
            command.Options.Add(option);
        }
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            string? manifestFile = parse.GetValue(manifest);
            string? hostFile = parse.GetValue(host);
            if (manifestFile is not null)
            {
                if (hostFile is not null || parse.GetValue(name) is not null || parse.GetValue(type) is not null
                    || parse.GetValue(address) is not null || parse.GetValue(auxiliary) is not null
                    || parse.GetValue(format) is not null)
                {
                    throw new DiskException("invalid_arguments", "--manifest cannot be combined with payload import options.", 2);
                }
                return Mutate("add", parse, image, write,
                    session => FileTransfer.Restore(session, manifestFile, _cancellationToken,
                        WriteDestination(parse, image, write)));
            }
            string contentFormat = parse.GetValue(format) ?? "binary";
            ValidateChoice(contentFormat, "--format", "binary", "text");
            string? fileType = parse.GetValue(type) ?? (contentFormat == "text" ? "TXT" : null);
            if (hostFile is null || parse.GetValue(name) is null || fileType is null)
            {
                throw new DiskException("invalid_arguments", "Payload import requires HOSTFILE, --name, and --type.", 2);
            }
            if (parse.GetValue(address) is not null && parse.GetValue(auxiliary) is not null)
            {
                throw new DiskException("invalid_arguments", "Specify --load-address or --aux-type, not both.", 2);
            }
            if (WriteDestination(parse, image, write) is { } destination)
            {
                ImageTransactions.EnsureDistinctPaths(hostFile, destination);
            }
            byte[] bytes = File.ReadAllBytes(hostFile);
            return Mutate("add", parse, image, write, session =>
            {
                if (session.Info.FileSystem == "dos33" && IsDosBinaryType(fileType)
                    && parse.GetValue(address) is null)
                {
                    throw new DiskException("invalid_arguments", "DOS binary import requires --load-address.", 2);
                }
                if ((session.Info.FileSystem == "dos33" && parse.GetValue(auxiliary) is not null)
                    || (session.Info.FileSystem == "prodos" && parse.GetValue(address) is not null))
                {
                    throw new DiskException("invalid_arguments", "Use --load-address for DOS and --aux-type for ProDOS.", 2);
                }
                if (parse.GetValue(address) is not null && !IsDosBinaryType(fileType))
                {
                    throw new DiskException("invalid_arguments", "--load-address requires a DOS binary file type.", 2);
                }
                byte[] payload = contentFormat == "text" ? EncodeText(bytes, session, fileType) : bytes;
                session.Add(parse.GetValue(name)!, payload, fileType,
                    ParseUShort(parse.GetValue(address) ?? parse.GetValue(auxiliary) ?? "0"));
            });
        });
        disk.Subcommands.Add(command);
    }

    private void AddMutationCommands(Command disk)
    {
        foreach (string name in new[] { "replace", "delete", "rename", "mkdir" })
        {
            Command command = new(name, name switch
            {
                "replace" => "Replace file payload while preserving its metadata.",
                "delete" => "Delete an image entry and reclaim its allocation.",
                "rename" => "Rename an image entry.",
                _ => "Create a ProDOS directory."
            });
            Argument<string> image = new("IMAGE");
            Argument<string> path = new("PATH");
            Argument<string> value = new(name == "replace" ? "HOSTFILE" : "NEWNAME");
            Option<bool> recursive = new("--recursive") { Description = "Explicitly delete directory contents." };
            Option<bool> parents = new("--parents") { Description = "Create missing ProDOS parent directories; existing directories are accepted." };
            Option<string> format = new("--format") { DefaultValueFactory = _ => "binary", Description = "binary payload or UTF-8 text." };
            command.Arguments.Add(image);
            command.Arguments.Add(path);
            if (name is "replace" or "rename")
            {
                command.Arguments.Add(value);
            }
            if (name == "delete")
            {
                command.Options.Add(recursive);
            }
            if (name == "mkdir")
            {
                command.Options.Add(parents);
            }
            if (name == "replace")
            {
                command.Options.Add(format);
            }
            WriteOptions write = AddWriteOptions(command);
            command.SetAction(parse => Mutate(name, parse, image, write, session =>
            {
                string entryPath = parse.GetValue(path)!;
                switch (name)
                {
                    case "replace":
                        string contentFormat = parse.GetValue(format)!;
                        ValidateChoice(contentFormat, "--format", "binary", "text");
                        if (WriteDestination(parse, image, write) is { } destination)
                        {
                            ImageTransactions.EnsureDistinctPaths(parse.GetValue(value)!, destination);
                        }
                        byte[] payload = File.ReadAllBytes(parse.GetValue(value)!);
                        if (contentFormat == "text")
                        {
                            payload = EncodeText(payload, session, session.GetEntry(entryPath).Type);
                        }
                        session.Replace(entryPath, payload);
                        break;
                    case "delete": session.Delete(entryPath, parse.GetValue(recursive)); break;
                    case "rename": session.Rename(entryPath, parse.GetValue(value)!); break;
                    default:
                        if (parse.GetValue(parents)) session.EnsureDirectory(entryPath);
                        else session.Mkdir(entryPath);
                        break;
                }
            }));
            disk.Subcommands.Add(command);
        }
    }

    private void AddAttributeCommand(Command disk)
    {
        Command command = new("attr", "Read or update file type, auxiliary type, and lock state.");
        Argument<string> image = new("IMAGE");
        Argument<string> path = new("PATH");
        Option<bool> lockFile = new("--lock");
        Option<bool> unlockFile = new("--unlock");
        Option<string?> type = new("--type");
        Option<string?> auxiliary = new("--aux-type");
        command.Arguments.Add(image);
        command.Arguments.Add(path);
        foreach (Option option in new Option[] { lockFile, unlockFile, type, auxiliary })
        {
            command.Options.Add(option);
        }
        WriteOptions write = AddWriteOptions(command);
        command.SetAction(parse =>
        {
            if (parse.GetValue(lockFile) && parse.GetValue(unlockFile))
            {
                throw new DiskException("invalid_arguments", "--lock and --unlock cannot be combined.", 2);
            }
            bool? locked = parse.GetValue(lockFile) ? true : parse.GetValue(unlockFile) ? false : null;
            if (locked is null && parse.GetValue(type) is null && parse.GetValue(auxiliary) is null)
            {
                if (parse.GetValue(write.Output) is not null || parse.GetValue(write.InPlace) || parse.GetValue(write.Overwrite))
                {
                    throw new DiskException("invalid_arguments", "No attribute changes were specified.", 2);
                }
                using DiskSession session = Open(parse.GetValue(image)!);
                DiskEntry entry = session.GetEntry(parse.GetValue(path)!);
                return Result("attr", entry, $"{entry.Path}: {entry.Type}, aux 0x{entry.AuxType:X4}, access 0x{entry.Access:X2}");
            }
            return Mutate("attr", parse, image, write, session => session.SetAttributes(parse.GetValue(path)!,
                locked, parse.GetValue(type), parse.GetValue(auxiliary) is { } aux ? ParseUShort(aux) : null));
        });
        disk.Subcommands.Add(command);
    }

    private void AddConvertCommand(Command disk)
    {
        Command command = new("convert", "Change container/layout without changing the filesystem.");
        Argument<string> input = new("INPUT");
        Argument<string> output = new("OUTPUT");
        Option<string> container = new("--container") { Required = true, Description = "raw or 2mg." };
        Option<string> order = new("--order") { Required = true, Description = "dos or prodos." };
        Option<bool> overwrite = new("--overwrite");
        Option<bool> loss = new("--allow-metadata-loss") { Description = "Allow dropping metadata that raw images cannot store." };
        command.Arguments.Add(input);
        command.Arguments.Add(output);
        foreach (Option option in new Option[] { container, order, overwrite, loss })
        {
            command.Options.Add(option);
        }
        command.SetAction(parse =>
        {
            ValidateChoice(parse.GetValue(container), "--container", "raw", "2mg");
            ValidateChoice(parse.GetValue(order), "--order", "dos", "prodos");
            ImageWriteResult result = ImageConverter.Convert(parse.GetValue(input)!, parse.GetValue(output)!,
                parse.GetValue(container)!, parse.GetValue(order)!, parse.GetValue(_inputOrder),
                parse.GetValue(overwrite), parse.GetValue(loss), _cancellationToken);
            return Result("convert", result, $"Converted to {result.OutputPath}");
        });
        disk.Subcommands.Add(command);
    }

    private int Mutate(string command, ParseResult parse, Argument<string> image, WriteOptions write,
        Action<DiskSession> operation)
    {
        string input = parse.GetValue(image)!;
        string order;
        string fs;
        using (DiskSession original = Open(input))
        {
            order = original.Info.Order;
            fs = original.Info.FileSystem;
        }
        ImageWriteResult result = ImageTransactions.Write(input, parse.GetValue(write.Output),
            parse.GetValue(write.InPlace), parse.GetValue(write.Overwrite), temporary =>
            {
                using DiskSession session = DiskSession.Open(temporary, order, fs, writable: true);
                operation(session);
            }, temporary => ValidateImage(temporary, order, fs), _cancellationToken);
        return Result(command, result, $"Wrote {result.OutputPath}"
            + (result.BackupPath is not null ? $" (backup: {result.BackupPath})" : ""));
    }

    private DiskSession Open(string path)
        => DiskSession.Open(path, _parse!.GetValue(_inputOrder), _parse.GetValue(_inputFs));

    private static void ValidateImage(string path, string order, string fs)
    {
        using DiskSession session = DiskSession.Open(path, order, fs);
        if (session.Info.IsDubious || session.Verify().Any(d => d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DiskException("post_write_validation", "The staged image failed structural validation.", 4);
        }
    }

    private int Result(string command, object data, string message)
    {
        if (_parse!.GetValue(_json))
        {
            CliResultEnvelope envelope = new(1, command, data, _pendingDiagnostics)
            {
                ResultSchemaId = command == "project.resolve"
                    ? CliContractSchemas.ProjectResolution : CliContractSchemas.Result
            };
            _output.WriteLine(JsonSerializer.Serialize(envelope, FileTransfer.JsonOptions));
        }
        else if (!_parse.GetValue(_quiet))
        {
            _output.WriteLine(message);
        }
        return 0;
    }

    private int Fail(string code, string message, int exitCode, IReadOnlyList<ProgramDiagnostic>? diagnostics = null)
    {
        if (_parse?.GetValue(_json) == true)
        {
            CliErrorEnvelope envelope = new(1, new(code, message, exitCode,
                _pendingDiagnostics.Concat(diagnostics ?? []).ToArray()));
            _error.WriteLine(JsonSerializer.Serialize(envelope, FileTransfer.JsonOptions));
        }
        else
        {
            ProgramDiagnostic[] details = _pendingDiagnostics.Concat(diagnostics ?? []).ToArray();
            if (!details.Any(diagnostic => diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(diagnostic.Message) && message.EndsWith(diagnostic.Message, StringComparison.Ordinal)))
            {
                _error.WriteLine($"{code}: {message}");
            }
            RenderProgramDiagnostics(details);
        }
        return exitCode;
    }

    private void EmitProgramDiagnostics(IReadOnlyList<ProgramDiagnostic> diagnostics)
    {
        if (_parse?.GetValue(_json) == true)
        {
            _pendingDiagnostics.AddRange(diagnostics);
            return;
        }
        RenderProgramDiagnostics(diagnostics);
    }

    private void RenderProgramDiagnostics(IEnumerable<ProgramDiagnostic> diagnostics)
    {
        foreach (ProgramDiagnostic diagnostic in diagnostics)
        {
            if (_parse?.GetValue(_verbose) != true &&
                !diagnostic.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) &&
                !diagnostic.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_renderedProgramDiagnostics.Add(diagnostic)) continue;
            List<string> location = [];
            if (!string.IsNullOrWhiteSpace(diagnostic.File)) location.Add(diagnostic.File);
            if (diagnostic.Line is { } line) location.Add($"source line {line}");
            if (diagnostic.Column is { } column) location.Add($"column {column}");
            if (diagnostic.BasicLine is { } basicLine) location.Add($"BASIC line {basicLine}");
            List<string> context = [];
            if (diagnostic.Symbol is not null) context.Add($"symbol: {diagnostic.Symbol}");
            if (diagnostic.Expected is not null) context.Add($"expected: {diagnostic.Expected}");
            if (diagnostic.Actual is not null) context.Add($"actual: {diagnostic.Actual}");
            string prefix = location.Count == 0 ? "" : string.Join(", ", location) + ": ";
            string suffix = context.Count == 0 ? "" : " (" + string.Join(", ", context) + ")";
            _error.WriteLine($"{prefix}{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}{suffix}");
        }
    }

    private void EmitDiagnostics(IReadOnlyList<DiskDiagnostic> diagnostics)
    {
        foreach (DiskDiagnostic diagnostic in diagnostics)
        {
            if (_parse!.GetValue(_json))
            {
                _pendingDiagnostics.Add(new(diagnostic.Code, diagnostic.Severity, diagnostic.Message));
                continue;
            }
            if (_parse!.GetValue(_verbose) || diagnostic.Severity is "warning" or "error")
            {
                _error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
            }
        }
    }

    private static WriteOptions AddWriteOptions(Command command)
    {
        WriteOptions options = new(new("--output") { Description = "Write to a separate output image." },
            new("--in-place") { Description = "Replace the input after validation and create a backup." },
            new("--overwrite") { Description = "Allow replacing an existing output file." });
        command.Options.Add(options.Output);
        command.Options.Add(options.InPlace);
        command.Options.Add(options.Overwrite);
        return options;
    }

    private static int ParseSize(string? value)
    {
        if (value is null)
        {
            return 140;
        }
        string normalized = value.ToLowerInvariant();
        int multiplier = normalized.EndsWith('m') ? 1024 : 1;
        if (normalized.EndsWith('m') || normalized.EndsWith('k'))
        {
            normalized = normalized[..^1];
        }
        int size = checked(int.Parse(normalized, CultureInfo.InvariantCulture) * multiplier);
        if (size <= 0)
        {
            throw new DiskException("invalid_size", "Disk size must be positive.", 2);
        }
        return size;
    }

    private static ushort ParseUShort(string value)
        => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ushort.Parse(value, CultureInfo.InvariantCulture);

    private static bool IsDosBinaryType(string value)
        => value.Equals("B", StringComparison.OrdinalIgnoreCase)
            || value.Equals("BIN", StringComparison.OrdinalIgnoreCase)
            || (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && byte.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte type)
                && type == 6);

    private static void ValidateChoice(string? value, string option, params string[] choices)
    {
        if (value is not null && !choices.Contains(value, StringComparer.Ordinal))
        {
            throw new DiskException("invalid_arguments", $"{option} must be one of: {string.Join(", ", choices)}.", 2);
        }
    }

    private sealed record WriteOptions(Option<string?> Output, Option<bool> InPlace, Option<bool> Overwrite);
}
