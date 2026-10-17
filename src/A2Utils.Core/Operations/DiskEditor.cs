// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Backends;

namespace A2Utils.Core.Operations;

/// <summary>Edits a staged disk and validates its reopened contents before an atomic commit.</summary>
public static class DiskEditor
{
    /// <summary>
    /// Owns the writable session, structural validation, optional content postconditions, and
    /// destination commit. Specify either outputPath or inPlace; in-place edits retain a backup.
    /// </summary>
    /// <remarks>
    /// The edit and validate callbacks must not retain or dispose their sessions. The validate
    /// callback receives a newly opened read-only session; throw to reject the candidate.
    /// All callbacks finish before the destination is replaced. A failed edit or validation
    /// leaves the input and any existing destination unchanged.
    /// </remarks>
    public static ImageWriteResult Edit(string inputPath, Action<DiskSession> edit,
        string? outputPath = null, bool inPlace = false, bool overwrite = false,
        string? inputOrder = null, string? inputFileSystem = null,
        Action<DiskSession>? validate = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        cancellationToken.ThrowIfCancellationRequested();
        ImageTransactions.ValidatePath(inputPath);
        DiskSession.ValidateImageFile(inputPath, cancellationToken);
        DiskInfo? original = null;
        return ImageTransactions.Write(inputPath, outputPath, inPlace, overwrite, temporary =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using DiskSession session = DiskSession.Open(temporary, inputOrder, inputFileSystem, writable: true);
            original = session.Info;
            edit(session);
        }, temporary =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using DiskSession session = DiskSession.Open(temporary, original!.Order, original.FileSystem);
            DiskInfo candidate = session.Info;
            if (candidate.Container != original.Container || candidate.Order != original.Order ||
                candidate.FileSystem != original.FileSystem || candidate.SizeBytes != original.SizeBytes ||
                candidate.IsDubious || candidate.Diagnostics.Any(diagnostic => diagnostic.Severity == "error"))
                throw new DiskException("edit.validation_failed", "The edited image failed structural or layout validation.", 4);
            validate?.Invoke(session);
        }, cancellationToken);
    }
}
