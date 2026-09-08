namespace A2Utils.Tests;

internal static class TestPaths
{
    // macOS exposes its temporary directory through /var, a link to /private/var.
    // Use a physical test root so intentional link-rejection tests exercise only their own links.
    public static string TemporaryRoot { get; } = ResolveDirectory(Path.GetTempPath());

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
