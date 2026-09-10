using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddCc65Commands(RootCommand root)
    {
        Command group = new("cc", "Compile Apple II C programs with an optional external cc65 installation.");
        Command compile = new("compile", "Run cl65 in an isolated source copy and emit a validated AppleSingle program.");
        Argument<string> source = new("INPUT") { Description = "Main C or ca65 assembly source." };
        Option<string> output = new("--to") { Required = true, Description = "AppleSingle output file." };
        Option<string> compiler = new("--compiler") { DefaultValueFactory = _ => "cl65", Description = "cl65 executable path or command name." };
        Option<string> target = new("--target") { DefaultValueFactory = _ => "apple2", Description = "apple2 or apple2enh." };
        Option<string?> version = new("--expected-version") { Description = "Require this exact cl65 --version result." };
        Option<string?> projectRoot = new("--project-root") { Description = "Source/include root; defaults to the input directory." };
        Option<int> timeout = new("--timeout") { DefaultValueFactory = _ => 60, Description = "Maximum seconds per compiler process (1..3600)." };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace an existing output after validation." };
        compile.Arguments.Add(source);
        foreach (Option option in new Option[] { output, compiler, target, version, projectRoot, timeout, overwrite }) compile.Options.Add(option);
        compile.SetAction(parse =>
        {
            string input = Path.GetFullPath(parse.GetValue(source)!);
            string destination = Path.GetFullPath(parse.GetValue(output)!);
            ImageTransactions.EnsureDistinctPaths(input, destination);
            string rootPath = parse.GetValue(projectRoot) is { } value ? Path.GetFullPath(value) : Path.GetDirectoryName(input)!;
            string compilerPath = parse.GetValue(compiler)!;
            if (compilerPath.Contains(Path.DirectorySeparatorChar) || compilerPath.Contains(Path.AltDirectorySeparatorChar))
                compilerPath = Path.GetFullPath(compilerPath);
            Cc65Result result = Cc65Compiler.Compile(input, new()
            {
                Compiler = compilerPath,
                Target = parse.GetValue(target)!,
                ExpectedVersion = parse.GetValue(version),
                TimeoutSeconds = parse.GetValue(timeout)
            }, rootPath, _cancellationToken);
            foreach (BuildInput dependency in result.Inputs)
                ImageTransactions.EnsureDistinctPaths(Path.GetFullPath(dependency.Path, rootPath), destination);
            ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                path => File.WriteAllBytes(path, result.AppleSingle), path =>
                {
                    byte[] bytes = ProgramFiles.ReadBytes(path, 128 * 1024, _cancellationToken);
                    if (!bytes.AsSpan().SequenceEqual(result.AppleSingle))
                        throw new DiskException("program.validation_failed", "Staged compiler output differs from the expected contents.", 4);
                    AppleSingleProgram.Decode(bytes);
                }, _cancellationToken);
            AppleSinglePayload payload = AppleSingleProgram.Decode(result.AppleSingle);
            return Result("cc.compile", new
            {
                written.OutputPath,
                format = "applesingle",
                result.Version,
                target = parse.GetValue(target),
                payload.FileType,
                payload.AuxType,
                payloadLength = payload.Bytes.Length,
                sha256 = ProgramFiles.Hash(result.AppleSingle),
                result.Map,
                result.Labels,
                result.Inputs
            }, $"Compiled {payload.Bytes.Length} program bytes to {written.OutputPath}");
        });
        group.Subcommands.Add(compile);
        root.Subcommands.Add(group);
    }
}
