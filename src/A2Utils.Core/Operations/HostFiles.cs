using System.Diagnostics;
using System.Globalization;

namespace A2Utils.Core.Operations;

/// <summary>Shared host-filesystem checks and physical-path resolution.</summary>
public static class HostFiles
{
    /// <summary>Resolves directory links so generated temporary files pass the write link policy.</summary>
    internal static string ResolvePhysicalDirectory(string path)
        => ResolvePhysicalDirectory(path, new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));

    private static string ResolvePhysicalDirectory(string path, HashSet<string> visited)
    {
        string fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath))
            throw new IOException("A directory-link cycle was found while resolving the path.");
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException("The directory does not exist: " + fullPath);

        string root = Path.GetPathRoot(fullPath)!;
        string resolved = root;
        foreach (string segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(resolved, segment));
            FileSystemInfo? target = directory.ResolveLinkTarget(returnFinalTarget: true);
            resolved = target is null ? directory.FullName
                : ResolvePhysicalDirectory(target.FullName, visited);
        }

        // A later path component may traverse the same link target again without a cycle.
        visited.Remove(fullPath);
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    /// <summary>
    /// Rejects Unix pipes, sockets, devices, and other nonregular entries before opening them.
    /// Windows callers use their existing file-attribute and stream checks.
    /// </summary>
    public static void EnsureRegularFile(string path, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // FileAttributes does not distinguish Unix pipes/devices from regular files.
        // Ask the OS stat utility for the mode before opening an entry that could block.
        bool mac = OperatingSystem.IsMacOS();
        if (!mac && !OperatingSystem.IsLinux())
        {
            throw new DiskException("unsupported_host_platform", "Directory import supports Windows, Linux, and macOS hosts.", 3);
        }
        ProcessStartInfo start = new("/usr/bin/stat")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(mac ? "-f" : "-c");
        start.ArgumentList.Add(mac ? "%p" : "%f");
        if (!mac)
        {
            start.ArgumentList.Add("--");
        }
        start.ArgumentList.Add(path);
        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("Could not inspect the host file type.");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using CancellationTokenRegistration registration = timeout.Token.Register(() =>
            {
                try
                {
                    process.Kill();
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The short-lived stat process may already have exited.
                }
            });
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DiskException("import_host_inspection_timeout", "The host file type inspection timed out.");
            }
            string modeText = output.GetAwaiter().GetResult().Trim();
            string errorText = error.GetAwaiter().GetResult().Trim();
            if (process.ExitCode != 0)
            {
                throw new IOException("Could not inspect the host file type: " + errorText);
            }
            uint mode = mac ? Convert.ToUInt32(modeText, 8) : uint.Parse(modeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if ((mode & 0xF000) != 0x8000)
            {
                throw new DiskException("unsupported_host_entry", "Import accepts regular host files only; pipes, sockets, and devices are refused.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new IOException("Directory import requires the standard /usr/bin/stat utility on Unix hosts.", exception);
        }
    }
}
