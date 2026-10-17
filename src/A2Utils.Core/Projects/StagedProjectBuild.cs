// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Security.Cryptography;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

/// <summary>Keeps a validated image until its input and destination evidence can be checked at commit.</summary>
internal sealed class StagedProjectBuild : IDisposable
{
    private readonly string _directory;
    private readonly string _destination;
    private readonly bool _overwrite;
    private readonly CancellationToken _cancellationToken;
    private readonly DestinationFingerprint? _destinationOriginal;
    private ProjectBuildResult? _preview;
    private Action? _validateInputs;
    private bool _disposed;
    private bool _committed;

    internal StagedProjectBuild(string destination, bool overwrite, CancellationToken cancellationToken)
    {
        _destination = destination;
        _overwrite = overwrite;
        _cancellationToken = cancellationToken;
        ImageTransactions.ValidatePath(destination);
        _destinationOriginal = CaptureDestination();
        _directory = Path.Combine(HostFiles.ResolvePhysicalDirectory(Path.GetTempPath()),
            $"a2-staged-build-{Guid.NewGuid():N}");
        ImageTransactions.ValidatePath(_directory);
        Directory.CreateDirectory(_directory);
        ImagePath = Path.Combine(_directory, "build.img");
    }

    internal string ImagePath { get; }
    internal ProjectBuildResult Preview => _preview
        ?? throw new InvalidOperationException("The staged build is not prepared.");

    internal void Initialize(ProjectBuildResult preview, Action validateInputs)
    {
        _preview = preview;
        _validateInputs = validateInputs;
    }

    internal ProjectBuildResult Commit(Action? validateExternalInputs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) throw new InvalidOperationException("The staged build has already been committed.");
        ProjectBuildResult preview = Preview;
        _cancellationToken.ThrowIfCancellationRequested();
        ValidateDestination();
        _validateInputs!();
        validateExternalInputs?.Invoke();
        Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
        ImageTransactions.Write(ImagePath, _destination, inPlace: false, _overwrite,
            static _ => { }, temporary =>
            {
                if (ProgramFiles.Hash(ProgramFiles.ReadBytes(temporary, 34 * 1024 * 1024,
                    _cancellationToken)) != preview.Sha256)
                    throw new DiskException("project.build_changed", "The staged image changed after validation; the output was not committed.", 6);
                _validateInputs();
                validateExternalInputs?.Invoke();
                ValidateDestination();
            }, _cancellationToken);
        _committed = true;
        return preview with { Preflight = false };
    }

    private void ValidateDestination()
    {
        ImageTransactions.ValidatePath(_destination);
        if (Directory.Exists(_destination) || CaptureDestination() != _destinationOriginal)
            throw new DiskException("write.concurrent_change", "The destination changed after project staging began.", 6);
    }

    private DestinationFingerprint? CaptureDestination()
    {
        if (!File.Exists(_destination)) return null;
        HostFiles.EnsureRegularFile(_destination, _cancellationToken);
        using FileStream input = new(_destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = input.Length;
        DateTime modified = File.GetLastWriteTimeUtc(_destination);
        string hash = Convert.ToHexString(SHA256.HashDataAsync(input,
            _cancellationToken).GetAwaiter().GetResult());
        return new(length, modified, hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cc65Compiler.CleanupGeneratedDirectoryAsync(_directory).GetAwaiter().GetResult();
    }

    private sealed record DestinationFingerprint(long Length, DateTime Modified, string Sha256);
}
