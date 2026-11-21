// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

using System.CommandLine;
using A2Utils.Core;
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
        Option<bool> smoke = new("--smoke") { Description = "Build and execute an original mailbox program using the configured template; retain immutable evidence." };
        Option<string?> artifacts = new("--artifacts") { Description = "New smoke evidence directory; defaults to a unique artifacts/environment-checks/run-* directory beside PROFILE." };
        check.Arguments.Add(checkProfile);
        check.Options.Add(smoke);
        check.Options.Add(artifacts);
        check.SetAction(parse =>
        {
            string profilePath = Path.GetFullPath(parse.GetValue(checkProfile)!);
            if (!parse.GetValue(smoke) && parse.GetValue(artifacts) is not null)
                throw new DiskException("setup.smoke_options", "--artifacts requires --smoke.", 2);
            string artifactPath = parse.GetValue(artifacts) ?? Path.Combine(Path.GetDirectoryName(profilePath)!,
                "artifacts", "environment-checks", "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            EnvironmentCheckResult result = parse.GetValue(smoke)
                ? DevelopmentEnvironment.SmokeAsync(profilePath, artifactPath, _cancellationToken).GetAwaiter().GetResult()
                : DevelopmentEnvironment.CheckAsync(DevelopmentEnvironmentProfile.Load(profilePath), _cancellationToken).GetAwaiter().GetResult();
            Result("env.check", result, string.Join(Environment.NewLine, result.Checks.Select(item => $"{(item.Passed ? "OK" : "FAIL")} {item.Code}: {item.Message}"))
                + (result.Smoke is { } evidence ? "\nEvidence: " + evidence.ArtifactDirectory : ""));
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
        Option<string> engine = new("--engine") { DefaultValueFactory = _ => "mame", Description = "mame for a full-machine starter, or cpu for an assembly routine needing no external tools. cpu requires --language asm and cannot use --environment or --bare-metal." };
        init.Arguments.Add(directory);
        init.Options.Add(language);
        init.Options.Add(profile);
        init.Options.Add(bareMetal);
        init.Options.Add(engine);
        init.SetAction(parse =>
        {
            ProjectStarterResult result = ProjectStarter.Create(parse.GetValue(directory)!, parse.GetValue(language)!, parse.GetValue(profile),
                _cancellationToken, parse.GetValue(bareMetal), parse.GetValue(engine)!);
            string manifestPath = QuoteCommandPath(Path.Combine(result.Directory, "project.a2.json"));
            List<string> nextCommands = [];
            if (parse.GetValue(engine) == "mame")
                nextCommands.Add("a2 env check " + QuoteCommandPath(parse.GetValue(profile) is { } environmentPath
                    ? Path.GetFullPath(environmentPath) : Path.Combine(result.Directory, "environment.json")));
            nextCommands.Add($"a2 build {manifestPath} --check");
            nextCommands.Add($"a2 build {manifestPath} --test");
            return Result("init", new
            {
                result.Directory,
                result.Language,
                result.NeedsEnvironmentConfiguration,
                result.Files,
                engine = parse.GetValue(engine),
                nextCommands
            },
                $"Created {result.Language} project at {result.Directory}"
                + (result.NeedsEnvironmentConfiguration ? "\nConfigure the environment profile's local paths first." : "")
                + "\nNext commands:\n" + string.Join(Environment.NewLine, nextCommands));
        });
        root.Subcommands.Add(init);
    }
}
