// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core.Tests;

internal sealed class FixtureWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(TestPaths.TemporaryRoot, "a2utils-fixture-tests");

    public FixtureWorkspace()
    {
        DirectoryPath = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string NewPath(string name) => Path.Combine(DirectoryPath, name);

    public string CopyDisk(string extension = "do", string? name = null)
    {
        string destination = NewPath(name ?? "disk." + extension);
        File.Copy(FindFixture("independent-dos33." + extension), destination);
        return destination;
    }

    public static string FindFixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", name);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Run these tests from a repository checkout containing tests/TestData.", name);
    }

    public void Dispose()
    {
        string absolutePath = Path.GetFullPath(DirectoryPath);
        string allowedRoot = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(allowedRoot, StringComparison.Ordinal) ||
            Path.GetFileName(absolutePath).Length != 32)
        {
            throw new InvalidOperationException("Refusing to remove a path outside the fixture test workspace.");
        }

        Directory.Delete(absolutePath, recursive: true);
    }
}
