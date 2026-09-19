using System.Diagnostics;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Tests;

public sealed class HostFilesTests
{
    [Fact]
    public void ResolvePhysicalDirectory_RepeatedLinkToAncestor_ResolvesFinitePath()
    {
        using FixtureWorkspace workspace = new();
        string physical = workspace.NewPath("physical");
        string nested = Path.Combine(physical, "nested");
        Directory.CreateDirectory(nested);
        string alias = Path.Combine(physical, "alias");
        CreateDirectoryLink(alias, physical);

        try
        {
            string resolved = HostFiles.ResolvePhysicalDirectory(
                Path.Combine(alias, "alias", "nested"));

            Assert.Equal(nested, resolved);
            ImageTransactions.ValidatePath(resolved);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        // Junctions exercise directory-link traversal without Windows symlink privileges.
        ProcessStartInfo start = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{path.Replace("'", "''")}' " +
            $"-Value '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using Process process = Process.Start(start)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Creating the test junction timed out.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
