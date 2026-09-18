using System.CommandLine;
using A2Utils.Core.Setup;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private void AddSetupCommands(RootCommand root)
    {
        Command environment = new("env", "Check and fingerprint a reusable local Apple II development environment.");
        root.Subcommands.Add(environment);
        Command check = new("check", "Check configured tools, pinned MAME version, ROMs and the OS template.");
        Argument<string> checkProfile = new("PROFILE");
        check.Arguments.Add(checkProfile);
        check.SetAction(parse =>
        {
            EnvironmentCheckResult result = DevelopmentEnvironment.CheckAsync(DevelopmentEnvironmentProfile.Load(parse.GetValue(checkProfile)!), _cancellationToken).GetAwaiter().GetResult();
            Result("env.check", result, string.Join(Environment.NewLine, result.Checks.Select(item => $"{(item.Passed ? "OK" : "FAIL")} {item.Code}: {item.Message}")));
            return result.Ready ? 0 : 1;
        });
        environment.Subcommands.Add(check);
        Command lockCommand = new("lock", "Fingerprint configured executables, ROM files, template and cc65 distribution into a new lockfile.");
        Argument<string> lockProfile = new("PROFILE");
        Option<string> output = new("--output") { Required = true, Description = "New lockfile path; existing files are preserved." };
        lockCommand.Arguments.Add(lockProfile);
        lockCommand.Options.Add(output);
        lockCommand.SetAction(parse =>
        {
            DevelopmentEnvironmentLock result = DevelopmentEnvironment.CreateLock(parse.GetValue(lockProfile)!, parse.GetValue(output)!, _cancellationToken);
            return Result("env.lock", result, $"Locked {result.Files.Count} environment files to {Path.GetFullPath(parse.GetValue(output)!)}");
        });
        environment.Subcommands.Add(lockCommand);
        Command init = new("init", "Create a BASIC, assembly or C starter with a deterministic execution test.");
        Argument<string> directory = new("DIRECTORY");
        Option<string> language = new("--language") { DefaultValueFactory = _ => "asm", Description = "asm, basic, or c." };
        Option<string?> profile = new("--environment") { Description = "Existing local environment profile; otherwise create an editable profile." };
        Option<bool> bareMetal = new("--bare-metal") { Description = "Create an original assembly boot disk that does not require an OS template." };
        init.Arguments.Add(directory);
        init.Options.Add(language);
        init.Options.Add(profile);
        init.Options.Add(bareMetal);
        init.SetAction(parse =>
        {
            ProjectStarterResult result = ProjectStarter.Create(parse.GetValue(directory)!, parse.GetValue(language)!, parse.GetValue(profile),
                _cancellationToken, parse.GetValue(bareMetal));
            return Result("init", result, $"Created {result.Language} project at {result.Directory}"
                + (result.NeedsEnvironmentConfiguration ? "\nCheck and configure the environment profile before building or running its test." : ""));
        });
        root.Subcommands.Add(init);
    }
}
