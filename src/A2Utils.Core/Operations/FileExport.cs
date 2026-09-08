using System.Security.Cryptography;
using A2Utils.Core.Backends;

namespace A2Utils.Core.Operations;

/// <summary>Exports one logical file payload to a host file with explicit optional text conversion.</summary>
public static class FileExport
{
    public static ImageWriteResult Export(DiskSession session, string imagePath, string destination,
        string format = "binary", bool overwrite = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedFormat = format.ToLowerInvariant();
        if (normalizedFormat is not ("binary" or "text"))
        {
            throw new DiskException("export.unsupported_format", "Export format must be binary or text.", 2);
        }

        string target = Path.GetFullPath(destination);
        ImageTransactions.EnsureDistinctPaths(session.Info.Path, target);

        DiskEntry entry = session.GetEntry(imagePath);
        if (entry.IsDirectory)
        {
            throw new DiskException("export.not_a_file", "Export requires a file entry.");
        }
        if (normalizedFormat == "text" && entry.FileType != 0x04)
        {
            throw new DiskException("text.unsupported_file_type", "Text export requires a DOS T or ProDOS TXT file.", 3);
        }

        byte[] logicalData = session.ReadFile(imagePath, raw: false);
        byte[] payload = normalizedFormat == "text" ? AppleTextCodec.Decode(logicalData) : logicalData;
        byte[] expectedHash = SHA256.HashData(payload);
        return ImageTransactions.Create(target, overwrite, temporary =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(payload);
        }, temporary =>
        {
            using FileStream output = File.OpenRead(temporary);
            if (output.Length != payload.LongLength || !SHA256.HashData(output).AsSpan().SequenceEqual(expectedHash))
            {
                throw new DiskException("export.validation_failed", "The staged export differs from its expected payload.", 4);
            }
        }, cancellationToken);
    }
}
