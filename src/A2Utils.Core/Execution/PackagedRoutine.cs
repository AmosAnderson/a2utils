// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Assembly;
using A2Utils.Core.Backends;
using A2Utils.Core.Programs;
using A2Utils.Core.Projects;

namespace A2Utils.Core.Execution;

internal sealed record PackagedRoutine(string ImagePath, string ImageSha256, string FileSystem,
    string? Order, BuiltFile File)
{
    internal static ExecutionSpec Bind(ExecutionSpec spec, ExecutionRoutine routine, ProjectBuildResult build)
    {
        if (spec.Engine != "cpu")
            throw new DiskException("execution.packaged_routine", "Packaged routines require a project CPU test.", 2);
        StringComparison comparison = build.FileSystem == "dos33" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        BuiltFile file = build.Files.SingleOrDefault(f => f.Path.Equals(routine.Source, comparison))
            ?? throw new DiskException("execution.packaged_program", $"The project contains no packaged file '{routine.Source}'.", 2);
        if (DiskSession.ParseFileType(file.Type) != 0x06 || file.MemoryBank != "main" ||
            file.Origin is not { } origin || origin != file.AuxType)
            throw new DiskException("execution.packaged_program", $"'{file.Path}' must be a main-memory BIN with a load address.", 2);
        if (routine.Origin != 0 && routine.Origin != origin)
            throw new DiskException("execution.packaged_origin", $"Routine origin disagrees with the load address of '{file.Path}'.", 2);
        int entry = routine.EntryPoint ?? file.EntryPoint ?? origin;
        if (routine.EntrySymbol is { } symbol)
        {
            if (routine.EntryPoint is not null || symbol.Length is 0 or > 128)
                throw new DiskException("execution.routine_symbol", "Specify one entryPoint or entrySymbol.", 2);
            entry = BuildExecution.ResolveAddress(file, symbol);
        }
        return spec with
        {
            Routine = routine with { Source = file.Path, Origin = origin, EntryPoint = entry, EntrySymbol = null },
            PackagedRoutine = new(build.OutputPath, build.Sha256, build.FileSystem, build.Order, file)
        };
    }

    internal AssemblyResult Read(CancellationToken cancellationToken)
    {
        ProjectInputSnapshot snapshot = ProjectInputSnapshot.Capture(ImagePath, DiskSession.MaximumImageFileSize, cancellationToken);
        ValidateHash(snapshot.Sha256);
        using TemporaryProjectInput copy = new(snapshot, "cpu-image");
        using DiskSession disk = DiskSession.Open(copy.Path, Order, FileSystem);
        DiskEntry entry = disk.GetEntry(File.Path);
        if (entry.IsDirectory || entry.HasResourceFork || entry.IsDubious || entry.FileType != 0x06 ||
            entry.AuxType != File.AuxType || entry.Length != File.Length)
            throw new DiskException("execution.packaged_metadata", $"Catalog metadata disagrees with the build for '{File.Path}'.", 6);
        byte[] bytes = disk.ReadFile(File.Path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProgramFiles.Hash(bytes).Equals(File.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("execution.packaged_hash", $"Packaged bytes disagree with the build for '{File.Path}'.", 6);
        return new(entry.AuxType, bytes)
        {
            Symbols = File.Symbols,
            SourceMap = BuildExecution.SourceEntries(File.SourceMap).Where(line => line.Length > 0 &&
                line.Address >= entry.AuxType && (long)line.Address + line.Length <= entry.AuxType + bytes.Length).ToArray()
        };
    }

    internal void ValidateImage(CancellationToken cancellationToken)
        => ValidateHash(ProjectWorkflow.HashExecutionInput(ImagePath, DiskSession.MaximumImageFileSize, cancellationToken));

    private void ValidateHash(string hash)
    {
        if (!hash.Equals(ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new DiskException("execution.disk_hash_mismatch", $"The packaged routine image has changed: {ImagePath}", 6);
    }
}
