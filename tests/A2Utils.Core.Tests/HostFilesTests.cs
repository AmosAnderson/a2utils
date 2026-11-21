// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

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
        string alias = CreateAncestorAlias(physical);

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

    private static string CreateAncestorAlias(string target)
    {
        string alias = Path.Combine(target, "alias");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return alias;
        }

        // Junctions exercise directory-link traversal without Windows symlink privileges.
        ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = target,
            Arguments = "/d /c mklink /J alias .",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Creating the test junction timed out.");
        }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return alias;
    }
}
