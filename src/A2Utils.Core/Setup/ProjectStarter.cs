// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Text;
using System.Text.Json;
using A2Utils.Core.Execution;
using A2Utils.Core.Operations;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Setup;

public sealed record ProjectStarterResult(string Directory, string Language, bool NeedsEnvironmentConfiguration,
    IReadOnlyList<string> Files);

/// <summary>Creates a new, reviewable project with deterministic mailbox assertions and no bundled OS or ROM assets.</summary>
public static class ProjectStarter
{
    public static ProjectStarterResult Create(string directory, string language = "asm", string? environmentProfile = null,
        CancellationToken cancellationToken = default, bool bareMetal = false)
    {
        if (language is not ("asm" or "basic" or "c")) throw new DiskException("setup.language", "language must be asm, basic, or c.", 2);
        if (bareMetal && language != "asm")
            throw new DiskException("setup.bare_metal_language", "Bare-metal starters currently require --language asm.", 2);
        string destination = Path.GetFullPath(directory);
        ImageTransactions.ValidatePath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new DiskException("setup.destination_exists", "Project initialization requires a new directory.", 2);
        DevelopmentEnvironmentProfile profile = environmentProfile is null ? new()
        {
            MamePath = "tools/mame",
            RomDirectory = "roms",
            TemplateImage = bareMetal ? null : "prodos.po",
            Cc65Path = language == "c" ? "tools/cc65/bin/cl65" : null,
            Cc65Root = language == "c" ? "tools/cc65" : null
        } : DevelopmentEnvironmentProfile.Load(environmentProfile);
        if (profile.Machine == "apple2")
            throw new DiskException("setup.starter_machine", "Project starters require apple2p, apple2e, apple2ee, or apple2c; the original apple2 has no project target profile.", 2);
        if (!bareMetal && profile.TemplateImage is null)
            throw new DiskException("setup.starter_template", "Starter execution requires templateImage in the environment profile, booting to an Applesoft prompt (BASIC.SYSTEM under ProDOS).", 2);
        if (language == "c" && (profile.Cc65Path is null || profile.Cc65Root is null))
            throw new DiskException("setup.starter_compiler", "The C starter requires cc65Path and cc65Root in the environment profile.", 2);
        string target = profile.Machine switch { "apple2ee" => "apple2enh", "apple2c" => "apple2c", "apple2e" => "apple2e", _ => "apple2plus" };
        string extension = language switch { "asm" => "asm", "basic" => "bas", _ => "c" };
        string source = bareMetal
            ? ".org $0800\n.byte 1\nstart: lda #$2a\n    sta $0300\n    lda #$a5\n    sta $0301\n    lda #$5a\n    sta $0302\ndone: jmp done\n"
            : language switch
            {
                "basic" => "10 HOME\n20 PRINT \"A2 STARTER\"\n30 POKE 768,42\n40 POKE 769,165\n50 POKE 770,90\n60 END\n",
                "c" => "int main(void)\n{\n    volatile unsigned char* result = (unsigned char*)0x0300;\n    result[0] = 42;\n    result[1] = 165;\n    result[2] = 90;\n    for (;;) { }\n    return 0;\n}\n",
                _ => ".org $2000\nstart: lda #$2a\n    sta $0300\n    lda #$a5\n    sta $0301\n    lda #$5a\n    sta $0302\ndone: jmp done\n"
            };
        string profileReference = environmentProfile is null ? "environment.json" : Path.GetFullPath(environmentProfile);
        ProjectManifest manifest = new()
        {
            Target = target,
            Environment = profileReference,
            Output = bareMetal ? "build/boot.do" : "build/program." + (profile.TemplateFileSystem == "dos33" ? "do" : "po"),
            Disk = bareMetal
                ? new() { FileSystem = "dos33", Order = "dos", Blocks = 280, Container = "raw", VolumeName = "A2BOOT" }
                : new() { FileSystem = profile.TemplateFileSystem },
            BasicWorkspaceBytes = language == "basic" ? 1024 : 0,
            Files = bareMetal ? [] : [new() { Source = "main." + extension, Path = "AI.MAIN", Kind = language == "c" ? "cc65" : language }],
            Boot = bareMetal ? new() { Source = "main.asm", Kind = "asm", Origin = 0x0800, Sectors = 1 } : null,
            Execution = new("suite.json")
        };
        double boot = profile.BootSeconds;
        ExecutionSpec execution = new()
        {
            Name = "starter mailbox",
            Environment = profileReference,
            Machine = profile.Machine,
            EmulatedSeconds = boot + 10,
            HostTimeoutSeconds = Math.Max(60, boot + 30),
            Keys = bareMetal ? [] : [new(boot, (language == "basic" ? "RUN" : "BRUN") + " AI.MAIN\r")],
            Until = new(770, 90, bareMetal ? 0 : boot + 1),
            Memory = [new(768, "2AA55A")],
            CheckBasicRuntime = language == "basic"
        };
        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            ["main." + extension] = source,
            ["project.a2.json"] = JsonSerializer.Serialize(manifest, ProjectJson.Options) + "\n",
            ["execution.json"] = JsonSerializer.Serialize(execution, ExecutionSpec.JsonOptions) + "\n",
            ["suite.json"] = "{\n  \"schemaVersion\": 1,\n  \"tests\": [\"execution.json\"]\n}\n",
            ["README.md"] = "# Apple II starter\n\n"
                + (bareMetal
                    ? "This project assembles an original 256-byte boot sector into a deterministic DOS-order floppy. Configure the environment profile with your local MAME 0.289 and lawfully supplied ROMs. No OS image, ROM, or external asset is installed or bundled.\n\n"
                    : "Configure the environment profile with your local MAME 0.289, ROMs, and bootable DOS/ProDOS template. The template must reach an Applesoft prompt; ProDOS needs BASIC.SYSTEM. No external assets are installed or bundled. Set bootSeconds to the template's startup time. The new program name AI.MAIN must not already exist in the template.\n\n")
                + $"```sh\na2 env check {(environmentProfile is null ? "environment.json" : "\"" + profileReference + "\"")} --json\n"
                + "a2 build project.a2.json --check --json\na2 build project.a2.json --test --artifacts runs/first --json\n```\n\n"
                + "The execution test succeeds only after the program writes the exact bytes 2A A5 5A to $0300–$0302. "
                + "Changing these bytes makes the test fail. Use a new artifact directory for every run. "
                + "After checking your environment, run `a2 env lock PROFILE --output environment.lock.json`, then set "
                + "`toolchainLock` in project.a2.json and execution.json to that lockfile to enforce reproducibility.\n"
        };
        if (environmentProfile is null) files.Add("environment.json", JsonSerializer.Serialize(profile, DevelopmentEnvironment.JsonOptions) + "\n");
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".a2-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(staging, file.Key), file.Value, new UTF8Encoding(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            bool needsConfiguration = environmentProfile is null || !File.Exists(profile.MamePath) || !Directory.Exists(profile.RomDirectory)
                || !bareMetal && !File.Exists(profile.TemplateImage)
                || language == "c" && (!File.Exists(profile.Cc65Path) || !Directory.Exists(profile.Cc65Root));
            return new(destination, language, needsConfiguration, files.Keys.Select(file => Path.Combine(destination, file)).ToArray());
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
