using System.Runtime.InteropServices;

namespace Sanova.Core.IO;

/// <summary>
/// File-system helpers that tolerate the transient sharing violations antivirus scanners
/// and indexers cause on Windows, and map raw I/O failures to explainable errors.
/// </summary>
public static class FileSystem
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(2000),
    ];

    public static bool IsTransient(Exception ex) => ex switch
    {
        IOException io => io.HResult is ErrorSharingViolation or ErrorLockViolation,
        UnauthorizedAccessException => true, // frequently a scanner holding the file briefly
        _ => false,
    };

    public static bool IsDiskFull(Exception ex) => ex is IOException io && io.HResult is ErrorDiskFull or ErrorHandleDiskFull;

    public static void WithRetry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < RetryDelays.Length)
            {
                Thread.Sleep(RetryDelays[attempt]);
            }
        }
    }

    public static void MoveFile(string source, string destination, bool overwrite)
    {
        SafePath.RejectReparsePoints(source);
        SafePath.RejectReparsePoints(destination);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        WithRetry(() =>
        {
            if (overwrite && File.Exists(destination))
            {
                ClearReadOnly(destination);
            }
            File.Move(source, destination, overwrite);
        });
    }

    public static void DeleteFile(string path)
    {
        SafePath.RejectReparsePoints(path);
        if (!File.Exists(path)) return;
        WithRetry(() =>
        {
            ClearReadOnly(path);
            File.Delete(path);
        });
    }

    public static bool TryDeleteFile(string path)
    {
        try
        {
            DeleteFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void DeleteDirectory(string path)
    {
        SafePath.RejectReparsePoints(path);
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }))
        {
            ClearReadOnly(file);
        }
        WithRetry(() => Directory.Delete(path, recursive: true));
    }

    public static bool TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes empty directories from <paramref name="startDirectory"/> upwards, stopping at <paramref name="root"/>.</summary>
    public static void DeleteEmptyParents(string root, string startDirectory)
    {
        var normalizedRoot = SafePath.NormalizeRoot(root);
        var current = SafePath.NormalizeRoot(startDirectory);
        while (SafePath.IsStrictlyUnder(normalizedRoot, current) && Directory.Exists(current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any()) break;
            try
            {
                Directory.Delete(current);
            }
            catch (IOException)
            {
                break;
            }
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The subsequent operation reports the real problem.
        }
    }

    public static void SetHidden(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Exists) info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic only.
        }
    }

    /// <summary>Finds the closest existing directory at or above <paramref name="path"/>.</summary>
    public static string? FindExistingAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current)) return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    /// <summary>Checks writability by creating and deleting a probe file in the nearest existing directory.</summary>
    public static bool IsWritable(string directory)
    {
        var existing = FindExistingAncestor(directory);
        if (existing is null) return false;
        var probe = Path.Combine(existing, $".sanova-write-test-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDeleteFile(probe);
        }
    }

    public static long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var existing = FindExistingAncestor(path);
            if (existing is null) return null;
            var root = Path.GetPathRoot(existing);
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }))
        {
            total += file.Length;
        }
        return total;
    }

    /// <summary>Locations that must never become an installation target.</summary>
    public static bool IsProtectedLocation(string path)
    {
        var full = SafePath.NormalizeRoot(path);
        if (Path.GetPathRoot(full) is { } root && string.Equals(SafePath.NormalizeRoot(root), full, SafePath.Comparison))
        {
            return true; // drive root
        }

        var protectedFolders = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        foreach (var folder in protectedFolders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            if (string.Equals(SafePath.NormalizeRoot(folder), full, SafePath.Comparison)) return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(windows) && SafePath.IsStrictlyUnder(windows, full);
    }

    public static SanovaException MapIoException(Exception ex, string path)
    {
        if (ex is SanovaException sanova) return sanova;
        if (IsDiskFull(ex))
        {
            return SanovaException.Wrap(ErrorCodes.DiskSpace, $"Disk full while writing '{path}'.", ex, ("path", path));
        }
        return ex switch
        {
            UnauthorizedAccessException => SanovaException.Wrap(ErrorCodes.AccessDenied, $"Access denied: '{path}'.", ex, ("path", path)),
            IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation
                => SanovaException.Wrap(ErrorCodes.FileInUse, $"File in use: '{path}'.", ex, ("path", path)),
            _ => SanovaException.Wrap(ErrorCodes.IoError, $"I/O error on '{path}': {ex.Message}", ex, ("path", path), ("detail", ex.Message)),
        };
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
