// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Tests;

internal static class TestPaths
{
    // macOS exposes its temporary directory through /var, a link to /private/var.
    // Use a physical test root so intentional link-rejection tests exercise only their own links.
    public static string TemporaryRoot { get; } = ResolveDirectory(Path.GetTempPath());

    public static string ExecutionHost()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root not found.");
        return Path.Combine(root.FullName, "tests", "A2Utils.ExecutionTestHost", "bin", configuration, "net10.0",
            "A2Utils.ExecutionTestHost" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    }

    public static string ResolveDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string resolved = root;
        foreach (string segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(resolved, segment));
            FileSystemInfo? target = directory.ResolveLinkTarget(returnFinalTarget: true);
            resolved = target is null ? directory.FullName : ResolveDirectory(target.FullName);
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
