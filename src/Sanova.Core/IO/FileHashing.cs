using System.Buffers;
using System.Security.Cryptography;

namespace Sanova.Core.IO;

/// <summary>SHA-256 helpers. All hashes are exchanged as lowercase hexadecimal strings.</summary>
public static class FileHashing
{
    public const int BufferSize = 1024 * 1024;

    public static bool IsValidSha256(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static bool HashEquals(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string ToHex(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    public static string Sha256(ReadOnlySpan<byte> data) => ToHex(SHA256.HashData(data));

    public static async Task<string> Sha256FileAsync(string path, IProgress<long>? bytesRead = null, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenSequentialRead(path);
        return await Sha256StreamAsync(stream, bytesRead, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> Sha256StreamAsync(Stream stream, IProgress<long>? bytesRead = null, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                bytesRead?.Report(read);
            }
            return ToHex(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Computes SHA-256 and MD5 in one pass (MD5 is used only to match community version lists).</summary>
    public static async Task<(string Sha256, string Md5)> Sha256AndMd5FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenSequentialRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
            return (ToHex(sha.GetHashAndReset()), ToHex(md5.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> while hashing.
    /// Throws when more than <paramref name="maxBytes"/> are produced (decompression-bomb guard).
    /// </summary>
    public static async Task<(string Sha256, long Length)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long? maxBytes,
        IProgress<long>? bytesCopied,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (maxBytes is { } limit && total > limit)
                {
                    throw SanovaException.Create(ErrorCodes.ArchiveTooLarge, $"Stream produced more than the expected {limit} bytes.");
                }
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesCopied?.Report(read);
            }
            return (ToHex(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Synchronous variant for streams that only read synchronously (e.g. zip entries over HTTP ranges).</summary>
    public static (string Sha256, long Length) CopyAndHash(Stream source, Stream destination, long? maxBytes, Action<long>? bytesCopied, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (maxBytes is { } limit && total > limit)
                {
                    throw SanovaException.Create(ErrorCodes.ArchiveTooLarge, $"Stream produced more than the expected {limit} bytes.");
                }
                hash.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
                bytesCopied?.Invoke(read);
            }
            return (ToHex(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static FileStream OpenSequentialRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
}
