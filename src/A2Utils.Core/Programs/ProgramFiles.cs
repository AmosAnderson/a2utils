using System.Security.Cryptography;
using System.Text;
using A2Utils.Core.Operations;

namespace A2Utils.Core.Programs;

/// <summary>Bounded, regular-file reads for development inputs.</summary>
public static class ProgramFiles
{
    public static byte[] ReadBytes(string path, int maximum = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImageTransactions.ValidatePath(path);
        HostFiles.EnsureRegularFile(path, cancellationToken);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum)
            throw new DiskException("program.too_large", $"Input exceeds {maximum} bytes: {path}", 3);
        byte[] bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw new DiskException("program.source_changed", $"Input changed while reading: {path}");
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    public static string DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff'); }
        catch (DecoderFallbackException exception)
        {
            throw new DiskException("program.invalid_utf8", "Development source must be valid UTF-8.", 2, exception);
        }
    }

    public static string ReadText(string path, CancellationToken cancellationToken = default)
        => DecodeText(ReadBytes(path, cancellationToken: cancellationToken));

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
