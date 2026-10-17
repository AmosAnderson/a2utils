// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.CommandLine;
using A2Utils.Core;

namespace A2Utils.Cli;

public sealed partial class CliApplication
{
    private static void AddHostOutputAliases(Command command, string name)
    {
        foreach (Option option in command.Options)
        {
            if (OptionPathRole(name, option.Name) is not ("output-file" or "output-directory")) continue;
            string? alias = option.Name switch { "--to" => "--output", "--output" => "--to", _ => null };
            if (alias is not null && !command.Options.Any(other => other.Name == alias || other.Aliases.Contains(alias)))
                option.Aliases.Add(alias);
        }
        foreach (Command child in command.Subcommands)
            AddHostOutputAliases(child, name + " " + child.Name);
    }

    private static string ResolveProjectPath(string? requested)
    {
        string root = Environment.CurrentDirectory;
        if (requested is not null) return Path.GetFullPath(requested, root);
        string conventional = Path.Combine(root, "project.a2.json");
        if (File.Exists(conventional)) return conventional;
        string[] candidates = Directory.EnumerateFiles(root, "*.a2.json", SearchOption.TopDirectoryOnly)
            .Take(2).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new DiskException("project.not_found",
                "No project.a2.json or *.a2.json was found in the current directory. Specify PROJECT or create a project with a2 init.", 2),
            _ => throw new DiskException("project.ambiguous",
                "More than one *.a2.json exists in the current directory. Specify PROJECT explicitly.", 2)
        };
    }

    private static string ResolveArtifactPath(string input, string? requested, bool createRunSubdirectory = false)
    {
        if (requested is not null && string.IsNullOrWhiteSpace(requested))
            throw new DiskException("execution.artifacts_required", "--artifacts requires a non-empty directory.", 2);
        string directory = requested is null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "artifacts", "runs")
            : Path.GetFullPath(requested);
        return requested is null || createRunSubdirectory
            ? Path.Combine(directory, $"run-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}")
            : directory;
    }

    private static string QuoteCommandPath(string path)
        => "'" + path.Replace("'", OperatingSystem.IsWindows() ? "''" : "'\\''", StringComparison.Ordinal) + "'";
}
