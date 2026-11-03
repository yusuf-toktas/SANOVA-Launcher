using System.Runtime.InteropServices;
using System.Text;

namespace Sanova.Core.IO;

/// <summary>
/// GTA San Andreas (2005) uses the ANSI Windows APIs. It cannot open its own files from a
/// folder whose path contains characters outside the system's ANSI code page — the path
/// comes back with '?' substitutions. Installation targets are checked with this helper.
/// </summary>
public static class PathEncoding
{
    private static readonly Lazy<Encoding?> AnsiEncoding = new(CreateAnsiEncoding);

    public static bool IsAnsiCompatible(string path)
    {
        var encoding = AnsiEncoding.Value;
        if (encoding is null) return path.All(c => c < 128);
        try
        {
            encoding.GetBytes(path);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static bool IsAscii(string path) => path.All(c => c < 128);

    private static Encoding? CreateAnsiEncoding()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = GetACP();
            if (codePage == 65001) return new UTF8Encoding(false, true);
            return Encoding.GetEncoding((int)codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
