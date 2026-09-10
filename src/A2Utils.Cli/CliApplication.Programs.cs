using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2Utils.Core;
using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private static readonly UTF8Encoding ProgramUtf8 = new(false, true);

    private void AddProgramCommands(RootCommand root)
    {
        foreach (bool basic in new[] { false, true })
        {
            string groupName = basic ? "basic" : "asm";
            Command group = new(groupName, basic
                ? "Tokenize and list Applesoft BASIC programs."
                : "Assemble source and disassemble 6502/65C02 machine code.");
            root.Subcommands.Add(group);
            if (basic) AddBasicToolsCommands(group);
            else AddAssemblyReportCommands(group);
            foreach (bool compile in new[] { true, false })
            {
                Command command = new(compile ? "compile" : "decompile", basic
                    ? (compile ? "Tokenize a numbered Applesoft source listing." : "Convert tokenized Applesoft into a source listing.")
                    : (compile ? "Assemble source into machine code." : "Disassemble bytes into reassemblable source; code and data are not inferred."));
                command.Aliases.Add(basic
                    ? (compile ? "tokenize" : "detokenize")
                    : (compile ? "assemble" : "disassemble"));
                Argument<string> input = new("INPUT") { Description = compile ? "UTF-8 source file." : "Host program file, or entry path with --from-image." };
                Option<string> output = new("--to") { Required = true, Description = "Host output file." };
                Option<string?> format = new("--format") { Description = "raw payload (default) or dos header plus payload; no sector padding." };
                Option<string?> origin = new("--origin") { Description = basic ? "BASIC memory start; defaults to 0x0801." : "16-bit load address; inferred from .org, DOS header, or image metadata." };
                Option<string> cpu = new("--cpu") { DefaultValueFactory = _ => "6502", Description = "6502, 65c02 (Apple enhanced IIe/IIc), or w65c02 (WDC with bit instructions, WAI/STP)." };
                Option<string?> fromImage = new("--from-image") { Description = "Read INPUT from this disk image using file-type and load-address metadata." };
                Option<bool> overwrite = new("--overwrite") { Description = "Allow replacing the host output file." };
                command.Arguments.Add(input);
                foreach (Option option in new Option[] { output, format, origin, overwrite }) command.Options.Add(option);
                if (!basic) command.Options.Add(cpu);
                if (!compile) command.Options.Add(fromImage);
                command.SetAction(parse =>
                {
                    string inputPath = parse.GetValue(input)!;
                    string outputPath = parse.GetValue(output)!;
                    string? image = compile ? null : parse.GetValue(fromImage);
                    string hostFormat = parse.GetValue(format) ?? "raw";
                    ValidateChoice(hostFormat, "--format", "raw", "dos");
                    CpuKind cpuKind = basic ? CpuKind.Mos6502 : ParseCpu(parse.GetValue(cpu)!);
                    ushort? requestedOrigin = parse.GetValue(origin) is { } value ? ParseProgramAddress(value) : null;
                    if (image is not null && parse.GetValue(format) is not null)
                        throw new DiskException("invalid_arguments", "--format applies to host program files and cannot be combined with --from-image.", 2);
                    if (image is null && (parse.GetValue(_inputOrder) is not null || parse.GetValue(_inputFs) is not null))
                        throw new DiskException("invalid_arguments", "Image layout and filesystem overrides require --from-image for program commands.", 2);
                    ImageTransactions.EnsureDistinctPaths(image ?? inputPath, outputPath);

                    ushort programOrigin;
                    byte[] result;
                    int payloadLength;
                    AssemblyResult? assemblyReport = null;
                    if (compile)
                    {
                        byte[] payload;
                        if (basic)
                        {
                            string source = ReadProgramText(inputPath);
                            programOrigin = requestedOrigin ?? ApplesoftBasic.DefaultOrigin;
                            payload = ApplesoftBasic.Compile(source, programOrigin, _cancellationToken);
                        }
                        else
                        {
                            AssemblyResult assembly = Assembler.AssembleFile(inputPath, requestedOrigin, cpuKind, _cancellationToken);
                            assemblyReport = assembly;
                            foreach (string dependency in assembly.Dependencies)
                                ImageTransactions.EnsureDistinctPaths(dependency, outputPath);
                            programOrigin = assembly.Origin;
                            payload = assembly.Bytes;
                        }
                        payloadLength = payload.Length;
                        result = hostFormat == "raw" ? payload : basic
                            ? ProgramFileFormat.EncodeDosBasic(payload)
                            : ProgramFileFormat.EncodeDosBinary(payload, programOrigin);
                    }
                    else
                    {
                        byte[] payload;
                        ushort? inferredOrigin = null;
                        if (image is not null)
                        {
                            using DiskSession session = Open(image);
                            DiskEntry entry = session.GetEntry(inputPath);
                            byte expectedType = basic ? (byte)0xfc : (byte)0x06;
                            if (entry.IsDirectory || entry.FileType != expectedType)
                                throw new DiskException("program.file_type", basic
                                    ? "Applesoft decompilation requires a DOS A or ProDOS BAS file."
                                    : "Machine-code disassembly requires a DOS B or ProDOS BIN file.", 3);
                            if (entry.Length > 65536)
                                throw new DiskException("program.too_large", "Program input exceeds the 64 KiB address space.", 3);
                            inferredOrigin = basic
                                ? (session.Info.FileSystem == "prodos" && entry.AuxType != 0 ? entry.AuxType : ApplesoftBasic.DefaultOrigin)
                                : entry.AuxType;
                            payload = session.ReadFile(inputPath);
                        }
                        else
                        {
                            payload = ReadProgramBytes(inputPath, 65540);
                            if (hostFormat == "dos")
                            {
                                if (basic) payload = ProgramFileFormat.DecodeDosBasic(payload);
                                else (inferredOrigin, payload) = ProgramFileFormat.DecodeDosBinary(payload);
                            }
                        }
                        programOrigin = requestedOrigin ?? inferredOrigin ?? (basic ? ApplesoftBasic.DefaultOrigin
                            : throw new DiskException("program.origin_required", "Raw machine code requires --origin because its load address is not stored in the file.", 2));
                        ProgramFileFormat.ValidateAddressRange(payload.Length, programOrigin);
                        payloadLength = payload.Length;
                        string source = basic
                            ? ApplesoftBasic.Decompile(payload, programOrigin, _cancellationToken)
                            : Disassembler.Disassemble(payload, programOrigin, cpuKind, _cancellationToken);
                        result = ProgramUtf8.GetBytes(source);
                    }

                    byte[] expectedHash = SHA256.HashData(result);
                    ImageWriteResult written = ImageTransactions.Create(outputPath, parse.GetValue(overwrite), temporary =>
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        if (assemblyReport is not null) ValidateAssemblyDependencies(assemblyReport);
                        using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        stream.Write(result);
                    }, temporary =>
                    {
                        using FileStream stream = File.OpenRead(temporary);
                        if (stream.Length != result.Length || !SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash))
                            throw new DiskException("program.validation_failed", "The staged program differs from its expected contents.", 4);
                        if (assemblyReport is not null) ValidateAssemblyDependencies(assemblyReport);
                    }, _cancellationToken);
                    return Result($"{groupName}.{command.Name}", new
                    {
                        written.OutputPath,
                        origin = programOrigin,
                        payloadLength,
                        outputLength = result.Length,
                        cpu = basic ? null : parse.GetValue(cpu),
                        format = compile ? hostFormat : "source",
                        symbols = assemblyReport?.Symbols,
                        sourceMap = assemblyReport?.SourceMap,
                        dependencies = assemblyReport?.DependencyHashes
                    }, $"Wrote {written.OutputPath} ({payloadLength} program bytes at ${programOrigin:X4})");
                });
                group.Subcommands.Add(command);
            }
        }
    }

    private void AddAssemblyReportCommands(Command group)
    {
        foreach (bool map in new[] { false, true })
        {
            Command command = new(map ? "map" : "listing", map
                ? "Assemble source and export symbols, source locations, and dependency hashes as JSON."
                : "Assemble source and export an address/byte/source listing.");
            Argument<string> input = new("INPUT") { Description = "UTF-8 assembly source." };
            Option<string> output = new("--to") { Required = true, Description = "Host report file." };
            Option<string?> origin = new("--origin") { Description = "16-bit program origin." };
            Option<string> cpu = new("--cpu") { DefaultValueFactory = _ => "6502", Description = "6502, 65c02, or w65c02." };
            Option<bool> overwrite = new("--overwrite") { Description = "Allow replacing the report file." };
            command.Arguments.Add(input);
            foreach (Option option in new Option[] { output, origin, cpu, overwrite }) command.Options.Add(option);
            command.SetAction(parse =>
            {
                if (parse.GetValue(_inputOrder) is not null || parse.GetValue(_inputFs) is not null)
                    throw new DiskException("invalid_arguments", "Image overrides do not apply to source reports.", 2);
                string inputPath = parse.GetValue(input)!;
                string outputPath = parse.GetValue(output)!;
                ImageTransactions.EnsureDistinctPaths(inputPath, outputPath);
                AssemblyResult assembly = Assembler.AssembleFile(inputPath,
                    parse.GetValue(origin) is { } address ? ParseProgramAddress(address) : null,
                    ParseCpu(parse.GetValue(cpu)!), _cancellationToken);
                foreach (string dependency in assembly.Dependencies)
                    ImageTransactions.EnsureDistinctPaths(dependency, outputPath);
                object report = new
                {
                    schemaVersion = 1,
                    assembly.Origin,
                    payloadLength = assembly.Bytes.Length,
                    cpu = parse.GetValue(cpu),
                    assembly.Symbols,
                    assembly.SourceMap,
                    dependencies = assembly.DependencyHashes
                };
                string source = map ? JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }) + "\n" : Assembler.CreateListing(assembly);
                byte[] bytes = ProgramUtf8.GetBytes(source);
                byte[] expected = SHA256.HashData(bytes);
                ImageWriteResult written = ImageTransactions.Create(outputPath, parse.GetValue(overwrite), temporary =>
                {
                    ValidateAssemblyDependencies(assembly);
                    File.WriteAllBytes(temporary, bytes);
                }, temporary =>
                {
                    using FileStream stream = File.OpenRead(temporary);
                    if (stream.Length != bytes.Length || !SHA256.HashData(stream).AsSpan().SequenceEqual(expected))
                        throw new DiskException("program.validation_failed", "The staged assembly report differs from its expected contents.", 4);
                    ValidateAssemblyDependencies(assembly);
                }, _cancellationToken);
                return Result("asm." + command.Name, new { written.OutputPath, assembly.Origin, payloadLength = assembly.Bytes.Length },
                    $"Wrote {written.OutputPath}");
            });
            group.Subcommands.Add(command);
        }
    }

    private void ValidateAssemblyDependencies(AssemblyResult assembly)
    {
        foreach ((string path, string expected) in assembly.DependencyHashes)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ImageTransactions.ValidatePath(path);
            HostFiles.EnsureRegularFile(path, _cancellationToken);
            using FileStream stream = File.OpenRead(path);
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(expected, StringComparison.Ordinal))
                throw new DiskException("program.source_changed", "An assembly source or binary include changed before output was committed.", 6);
        }
    }

    private static CpuKind ParseCpu(string value) => value switch
    {
        "6502" => CpuKind.Mos6502,
        "65c02" => CpuKind.Apple65C02,
        "w65c02" => CpuKind.Wdc65C02,
        _ => throw new DiskException("invalid_arguments", "--cpu must be 6502, 65c02, or w65c02.", 2)
    };

    private static ushort ParseProgramAddress(string value)
        => value.StartsWith('$') ? ushort.Parse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ParseUShort(value);

    private string ReadProgramText(string path)
    {
        byte[] bytes = ReadProgramBytes(path, 4 * 1024 * 1024);
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith("\ufeff"u8)) content = content[3..];
        try { return ProgramUtf8.GetString(content); }
        catch (DecoderFallbackException exception)
        {
            throw new DiskException("program.invalid_utf8", "Program source must be valid UTF-8.", 2, exception);
        }
    }

    private byte[] ReadProgramBytes(string path, int maximum)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        HostFiles.EnsureRegularFile(Path.GetFullPath(path), _cancellationToken);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum)
            throw new DiskException("program.too_large", $"Input exceeds the {maximum:N0}-byte program limit.", 3);
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw new DiskException("program.source_changed", "The input grew while it was being read.");
        _cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
}
