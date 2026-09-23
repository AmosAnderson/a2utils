// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Operations;

namespace A2Utils.Cli.Tests;

public sealed class TextWorkflowRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(TestPaths.TemporaryRoot, $"a2-text-regression-{Guid.NewGuid():N}");

    public TextWorkflowRegressionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    public void TextMutation_OutputAliasesHostPayload_RefusesWithoutReplacingInputFiles(string operation)
    {
        string image = Path.Combine(_directory, "source.do");
        string host = Path.Combine(_directory, "input.txt");
        File.Copy(FindFixture(), image);
        File.WriteAllBytes(host, "IMPORTANT HOST TEXT\n"u8.ToArray());
        byte[] imageBefore = File.ReadAllBytes(image);
        byte[] hostBefore = File.ReadAllBytes(host);
        string[] args = operation == "add"
            ? ["disk", "add", image, host, "--name", "NEW", "--format", "text", "--output", host, "--overwrite"]
            : ["disk", "replace", image, "README", host, "--format", "text", "--output", host, "--overwrite"];

        (int code, string error) = Run(args);

        Assert.Equal(6, code);
        Assert.Contains("write.source_alias", error);
        Assert.Equal(hostBefore, File.ReadAllBytes(host));
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void Import_EmptyDirectoryWithTextFormatAndBinaryType_RefusesIncompatibleArguments()
    {
        string image = Path.Combine(_directory, "source.po");
        string host = Path.Combine(_directory, "empty");
        Directory.CreateDirectory(host);
        DiskSession.Create(image, "prodos", order: "prodos");
        byte[] before = File.ReadAllBytes(image);

        (int code, string error) = Run("disk", "import", image, host, "--format", "text", "--type", "BIN", "--in-place");

        Assert.Equal(2, code);
        Assert.Contains("invalid_arguments", error);
        Assert.Equal(before, File.ReadAllBytes(image));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("payload")]
    public void ManifestRestore_OutputAliasesAnInput_RefusesWithoutReplacingAnySource(string alias)
    {
        string extracted = Path.Combine(_directory, "extracted");
        ExtractionManifest manifest;
        using (DiskSession original = DiskSession.Open(FindFixture()))
        {
            manifest = FileTransfer.Extract(original, extracted);
        }

        string manifestPath = Path.Combine(extracted, FileTransfer.ManifestName);
        string output = alias == "manifest" ? manifestPath : Path.Combine(extracted, manifest.Entries[0].HostFile!);
        Dictionary<string, byte[]> originalFiles = Directory.GetFiles(extracted)
            .ToDictionary(path => path, File.ReadAllBytes);
        string image = Path.Combine(_directory, "source.do");
        DiskSession.Create(image, "dos33");
        byte[] imageBefore = File.ReadAllBytes(image);

        (int code, string error) = Run("disk", "add", image, "--manifest", manifestPath,
            "--output", output, "--overwrite");

        Assert.Equal(6, code);
        Assert.Contains("write.source_alias", error);
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(originalFiles.Count, Directory.GetFiles(extracted).Length);
        foreach ((string path, byte[] contents) in originalFiles)
        {
            Assert.Equal(contents, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("import")]
    public void TextImport_DosLoadAddress_RefusesUnrepresentableMetadata(string operation)
    {
        string image = Path.Combine(_directory, "source.do");
        File.Copy(FindFixture(), image);
        byte[] imageBefore = File.ReadAllBytes(image);
        string host = Path.Combine(_directory, "host");
        Directory.CreateDirectory(host);
        string hostFile = Path.Combine(host, "TEXT");
        byte[] hostBytes = "ONE\nTWO\n"u8.ToArray();
        File.WriteAllBytes(hostFile, hostBytes);
        string output = Path.Combine(_directory, "output.do");
        string[] args = operation == "add"
            ? ["disk", "add", image, hostFile, "--name", "TEXT", "--format", "text", "--load-address", "0x2000", "--output", output]
            : ["disk", "import", image, host, "--format", "text", "--load-address", "0x2000", "--output", output];

        (int code, string error) = Run(args);

        Assert.Equal(2, code);
        Assert.Contains("invalid_arguments", error);
        Assert.Contains("--load-address", error);
        Assert.False(File.Exists(output));
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(hostBytes, File.ReadAllBytes(hostFile));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void Import_OutputInsideHostTree_RefusesBeforeCreatingStagingFiles()
    {
        string image = Path.Combine(_directory, "source.do");
        File.Copy(FindFixture(), image);
        byte[] imageBefore = File.ReadAllBytes(image);
        string host = Path.Combine(_directory, "host");
        Directory.CreateDirectory(host);
        string hostFile = Path.Combine(host, "TEXT");
        byte[] hostBytes = "HOST TEXT\n"u8.ToArray();
        File.WriteAllBytes(hostFile, hostBytes);
        string output = Path.Combine(host, "output.do");

        (int code, string error) = Run("disk", "import", image, host, "--format", "text", "--output", output);

        Assert.Equal(6, code);
        Assert.Contains("import_output_in_source", error);
        Assert.False(File.Exists(output));
        Assert.Equal(imageBefore, File.ReadAllBytes(image));
        Assert.Equal(hostBytes, File.ReadAllBytes(hostFile));
        Assert.Single(Directory.GetFiles(host));
        Assert.Single(Directory.GetFiles(_directory));
    }

    private static (int Code, string Error) Run(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        return (CliApplication.Run(args, output, error), error.ToString());
    }

    private static string FindFixture()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "TestData", "independent-dos33.do");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Independent disk fixture was not found.");
    }

    public void Dispose()
    {
        string absolutePath = Path.GetFullPath(_directory);
        if (!string.Equals(Path.GetDirectoryName(absolutePath), TestPaths.TemporaryRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolutePath).StartsWith("a2-text-regression-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove a path outside the text workflow test workspace.");
        }

        Directory.Delete(absolutePath, recursive: true);
    }
}
