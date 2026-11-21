// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Tests;

public sealed class ShrinkItArchiveTests
{
    [Fact]
    public void Extract_NamedRecordAndForks_PreservesContentsMetadataAndArchive()
    {
        using var workspace = new FixtureWorkspace();
        string source = workspace.NewPath("no-extension");
        File.Copy(FixtureWorkspace.FindFixture("independent-files.shk"), source);
        byte[] original = File.ReadAllBytes(source);
        using var archive = ShrinkItArchive.Open(source);
        Assert.Equal(ProgramFiles.Hash(original), archive.Listing.Sha256);
        var first = archive.Listing.Entries[0];
        Assert.Equal("DOCS:HELLO", first.Name);
        Assert.Equal(1, first.Index);
        Assert.Equal(4, first.FileType);
        Assert.Equal(0x1234, first.AuxType);
        Assert.Equal(0xc3, first.Access);
        Assert.Equal(["data", "resource"], first.Parts.Select(p => p.Name));
        string output = workspace.NewPath("chosen-name");
        archive.Extract(output, first.Name);
        Assert.Equal("Hello, ShrinkIt!\r"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Equal("write.destination_exists", Assert.Throws<DiskException>(() =>
            archive.Extract(output, first.Name, part: "resource")).Code);
        var resource = archive.Extract(output, first.Name, part: "resource", overwrite: true);
        Assert.Equal(new byte[] { 0, 0x7f, 0x80, 0xff }, File.ReadAllBytes(output));
        Assert.Equal(ProgramFiles.Hash(File.ReadAllBytes(output)), resource.Sha256);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal("write.source_alias", Assert.Throws<DiskException>(() =>
            archive.Extract(source, first.Name, overwrite: true)).Code);
    }

    [Fact]
    public void Extract_DuplicateNames_RequiresIndexAndKeepsEmptyFork()
    {
        using var workspace = new FixtureWorkspace();
        using var archive = ShrinkItArchive.Open(FixtureWorkspace.FindFixture("independent-files.shk"));
        string path = workspace.NewPath("out");
        Assert.Equal("archive.ambiguous_entry", Assert.Throws<DiskException>(() => archive.Extract(path, "EMPTY")).Code);
        Assert.Equal("archive.ambiguous_entry", Assert.Throws<DiskException>(() => archive.Extract(path)).Code);
        Assert.Equal("archive.part_not_found", Assert.Throws<DiskException>(() => archive.Extract(path, index: 2, part: "resource")).Code);
        Assert.False(File.Exists(path));
        Assert.Equal(0, archive.Extract(path, index: 2).Length);
        archive.Extract(path, index: 3, overwrite: true);
        Assert.Equal("2"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void Extract_OriginalShrinkItLzw1_DecodesPartialBlockAndChecksEmbeddedCrc()
    {
        using var workspace = new FixtureWorkspace();
        using var archive = ShrinkItArchive.Open(FixtureWorkspace.FindFixture("independent-files.shk"));
        string output = workspace.NewPath("legacy");
        var result = archive.Extract(output, "LEGACY");
        Assert.Equal(string.Concat(Enumerable.Repeat("6502", 20)), File.ReadAllText(output));
        Assert.Equal(80, result.Length);

        byte[] damaged = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-files.shk"));
        // The legacy fixture has its in-header name followed by a single 16-byte thread header.
        int crcOffset = damaged.AsSpan().IndexOf("LEGACY"u8) + "LEGACY"u8.Length + 16;
        damaged[crcOffset] ^= 1;
        string source = workspace.NewPath("bad-crc.shk");
        File.WriteAllBytes(source, damaged);
        using var corrupt = ShrinkItArchive.Open(source);
        Assert.Equal("archive.invalid_part", Assert.Throws<DiskException>(() =>
            corrupt.Extract(output, "LEGACY", overwrite: true)).Code);
        Assert.Equal(80, new FileInfo(output).Length);
    }

    [Theory]
    [InlineData("ONLY.RSRC", "data")]
    [InlineData("ONLY.DATA", "resource")]
    public void Extract_ImplicitEmptyFork_UsesArchivedStorageType(string entry, string part)
    {
        using var workspace = new FixtureWorkspace();
        using var archive = ShrinkItArchive.Open(FixtureWorkspace.FindFixture("independent-files.shk"));
        var record = Assert.Single(archive.Listing.Entries, e => e.Name == entry);
        Assert.Equal(0, Assert.Single(record.Parts, p => p.Name == part).Length);
        string output = workspace.NewPath("empty");
        archive.Extract(output, entry, part: part);
        Assert.Empty(File.ReadAllBytes(output));
    }

    [Fact]
    public void Catalog_CompressedSdk_MatchesOriginalFilesystemAndExtractedBytes()
    {
        using var workspace = new FixtureWorkspace();
        using var archive = ShrinkItArchive.Open(FixtureWorkspace.FindFixture("independent-disk.sdk"));
        var disk = Assert.Single(Assert.Single(archive.Listing.Entries).Parts);
        Assert.Equal("NuLZW2", disk.Compression);
        Assert.True(disk.StoredLength < disk.Length);
        var catalog = archive.Catalog();
        Assert.Equal("dos33", catalog.FileSystem);
        Assert.Equal("prodos", catalog.Order);
        Assert.Equal(["HELLO.BIN", "README"], catalog.Entries.Select(e => e.Name));
        byte[] original = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-dos33.po"));
        string output = workspace.NewPath("disk.po");
        var result = archive.Extract(output);
        Assert.Equal("disk", result.Part);
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Equal(ProgramFiles.Hash(original), catalog.Sha256);
    }

    [Fact]
    public void Extract_BadChecksumAndCancellation_PreserveDestination()
    {
        using var workspace = new FixtureWorkspace();
        string path = workspace.NewPath("damaged.shk");
        byte[] damaged = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-files.shk"));
        int offset = damaged.AsSpan().IndexOf("Hello, ShrinkIt!"u8);
        Assert.True(offset > 0);
        damaged[offset] ^= 1;
        File.WriteAllBytes(path, damaged);
        using var archive = ShrinkItArchive.Open(path);
        string output = workspace.NewPath("existing");
        File.WriteAllText(output, "keep");
        Assert.Equal("archive.invalid_part", Assert.Throws<DiskException>(() =>
            archive.Extract(output, index: 1, overwrite: true)).Code);
        Assert.Throws<OperationCanceledException>(() =>
            archive.Extract(output, index: 2, overwrite: true, cancellationToken: new(true)));
        Assert.Equal("keep", File.ReadAllText(output));
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void Open_TruncatedArchive_RejectsPartialListing()
    {
        using var workspace = new FixtureWorkspace();
        byte[] bytes = File.ReadAllBytes(FixtureWorkspace.FindFixture("independent-files.shk"));
        string path = workspace.NewPath("truncated");
        File.WriteAllBytes(path, bytes[..^1]);
        Assert.Equal("archive.invalid", Assert.Throws<DiskException>(() => ShrinkItArchive.Open(path)).Code);
    }
}
