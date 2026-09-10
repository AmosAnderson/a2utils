using System.CommandLine;
using A2Utils.Core;
using A2Utils.Core.Basic;
using A2Utils.Core.Operations;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddBasicToolsCommands(Command group)
    {
        Command check = new("check", "Check numbered Applesoft source for common syntax and line-reference mistakes.");
        Argument<string> checkInput = new("INPUT") { Description = "Numbered UTF-8 Applesoft source." };
        check.Arguments.Add(checkInput);
        check.SetAction(parse =>
        {
            string input = parse.GetValue(checkInput)!;
            BasicCheckResult result = ApplesoftTools.Check(ReadProgramText(input), input, _cancellationToken);
            Result("basic.check", result, result.Valid ? "BASIC checks passed." : "BASIC checks found errors.");
            if (!parse.GetValue(_json))
            {
                foreach (var diagnostic in result.Diagnostics)
                    _error.WriteLine($"{diagnostic.Code}: {diagnostic.Message} (source line {diagnostic.Line})");
            }
            return result.Valid ? 0 : 2;
        });
        group.Subcommands.Add(check);

        Command renumber = new("renumber", "Renumber numbered source and literal branch targets, preserving comments and data.");
        Argument<string> inputArgument = new("INPUT") { Description = "Numbered UTF-8 Applesoft source." };
        Option<string> output = new("--to") { Required = true, Description = "Host source output file." };
        Option<int> start = new("--start") { DefaultValueFactory = _ => 10, Description = "First new line number (0..63999)." };
        Option<int> step = new("--step") { DefaultValueFactory = _ => 10, Description = "Positive line-number increment." };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace an existing host output after validation." };
        renumber.Arguments.Add(inputArgument);
        foreach (Option option in new Option[] { output, start, step, overwrite }) renumber.Options.Add(option);
        renumber.SetAction(parse =>
        {
            string input = parse.GetValue(inputArgument)!;
            string destination = parse.GetValue(output)!;
            ImageTransactions.EnsureDistinctPaths(input, destination);
            BasicRenumberResult result = ApplesoftTools.Renumber(ReadProgramText(input), parse.GetValue(start), parse.GetValue(step), input, _cancellationToken);
            byte[] bytes = ProgramUtf8.GetBytes(result.Source);
            ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                temporary => File.WriteAllBytes(temporary, bytes), temporary =>
                {
                    if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes))
                        throw new DiskException("program.validation_failed", "Staged BASIC source differs from the expected contents.", 4);
                    ApplesoftBasic.Compile(ProgramUtf8.GetString(File.ReadAllBytes(temporary)), cancellationToken: _cancellationToken);
                }, _cancellationToken);
            return Result("basic.renumber", new { written.OutputPath, result.Mapping, result.Diagnostics },
                $"Renumbered {result.Mapping.Count} lines to {written.OutputPath}");
        });
        group.Subcommands.Add(renumber);
        AddBasicPrepareCommand(group);
    }

    private void AddBasicPrepareCommand(Command group)
    {
        Command prepare = new("prepare", "Expand unnumbered source with @labels into ordinary numbered Applesoft.");
        Argument<string> input = new("INPUT") { Description = "Unnumbered UTF-8 BASIC source with optional @label: definitions." };
        Option<string> output = new("--to") { Required = true, Description = "Numbered source output file." };
        Option<int> start = new("--start") { DefaultValueFactory = _ => 10, Description = "First generated line number (0..63999)." };
        Option<int> step = new("--step") { DefaultValueFactory = _ => 10, Description = "Positive line-number increment." };
        Option<bool> overwrite = new("--overwrite") { Description = "Replace an existing host output after validation." };
        prepare.Arguments.Add(input);
        foreach (Option option in new Option[] { output, start, step, overwrite }) prepare.Options.Add(option);
        prepare.SetAction(parse =>
        {
            string source = parse.GetValue(input)!;
            string destination = parse.GetValue(output)!;
            ImageTransactions.EnsureDistinctPaths(source, destination);
            BasicPrepareResult result = ApplesoftTools.Prepare(ReadProgramText(source), parse.GetValue(start), parse.GetValue(step), source, _cancellationToken);
            byte[] bytes = ProgramUtf8.GetBytes(result.Source);
            ImageWriteResult written = ImageTransactions.Create(destination, parse.GetValue(overwrite),
                temporary => File.WriteAllBytes(temporary, bytes), temporary =>
                {
                    byte[] staged = File.ReadAllBytes(temporary);
                    if (!staged.AsSpan().SequenceEqual(bytes))
                        throw new DiskException("program.validation_failed", "Staged BASIC source differs from the expected contents.", 4);
                    ApplesoftBasic.Compile(ProgramUtf8.GetString(staged), cancellationToken: _cancellationToken);
                }, _cancellationToken);
            return Result("basic.prepare", new { written.OutputPath, result.Mapping, result.Diagnostics },
                $"Prepared {result.Mapping.Count} BASIC lines in {written.OutputPath}");
        });
        group.Subcommands.Add(prepare);
    }
}
