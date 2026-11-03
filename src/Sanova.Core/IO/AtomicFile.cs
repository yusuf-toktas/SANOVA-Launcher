using System.Text;
using System.Text.Json;

namespace Sanova.Core.IO;

/// <summary>
/// Crash-safe file replacement: content is written to a temporary sibling, flushed to
/// disk and then moved over the destination, so readers see either the old or the new
/// file — never a truncated one.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        SafePath.RejectReparsePoints(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            FileSystem.MoveFile(temp, path, overwrite: true);
        }
        catch
        {
            FileSystem.TryDeleteFile(temp);
            throw;
        }
    }

    public static void WriteAllText(string path, string content) => WriteAllBytes(path, Utf8NoBom.GetBytes(content));

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null)
        => WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, options ?? JsonDefaults.Indented));

    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, options ?? JsonDefaults.Indented);
    }
}
