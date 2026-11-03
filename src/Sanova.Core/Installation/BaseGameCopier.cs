using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sanova.Core.Game;
using Sanova.Core.IO;

namespace Sanova.Core.Installation;

public sealed record BaseCopyResult(Dictionary<string, InstalledFile> Files, IReadOnlyList<string> ReferenceMismatches);

/// <summary>
/// Copies the player's original game into the SANOVA folder. The original installation is
/// only ever read. Files are hashed while copying (no second pass), timestamps are preserved,
/// and progress is checkpointed so an interrupted copy resumes where it stopped.
/// </summary>
public sealed class BaseGameCopier
{
    private readonly ILogger<BaseGameCopier> _logger;

    public BaseGameCopier(ILogger<BaseGameCopier> logger)
    {
        _logger = logger;
    }

    /// <summary>Files to copy: the reference list when one matches, otherwise everything except launcher metadata.</summary>
    public static IReadOnlyList<(string Path, long Size, string? Sha256)> SelectFiles(string sourceDirectory, BaseGameReference? reference)
    {
        if (reference is not null && reference.Files.Count > 0)
        {
            return reference.Files.Select(f => (SafePath.NormalizeRelative(f.Path), f.Size, f.Sha256)).ToList();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var result = new List<(string, long, string?)>();
        foreach (var file in new DirectoryInfo(sourceDirectory).EnumerateFiles("*", options))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file.FullName).Replace('\\', '/');
            if (relative.StartsWith(InstallStateStore.MetadataFolderName + "/", SafePath.Comparison)) continue;
            if (!SafePath.TryNormalizeRelative(relative, out var normalized, out _)) continue;
            result.Add((normalized, file.Length, null));
        }
        return result;
    }

    public async Task<BaseCopyResult> CopyAsync(string sourceDirectory, string targetDirectory, BaseGameReference? reference,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var files = SelectFiles(sourceDirectory, reference);
        var total = files.Sum(f => f.Size);
        var checkpointPath = InstallStateStore.BaseCopyProgressPath(targetDirectory);
        var completed = LoadCheckpoint(checkpointPath);
        var records = new Dictionary<string, InstalledFile>(SafePath.Comparer);
        var mismatches = new List<string>();
        long done = 0;
        var checkpointClock = Stopwatch.StartNew();
        var index = 0;

        foreach (var (path, expectedSize, expectedSha) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var source = SafePath.GetFullPathUnder(sourceDirectory, path);
            var target = SafePath.GetFullPathUnder(targetDirectory, path);

            if (completed.TryGetValue(path, out var previous) && File.Exists(target) && new FileInfo(target).Length == previous.Size)
            {
                records[path] = previous;
                done += previous.Size;
                progress?.Report(new OperationProgress(OperationStage.CopyingGameFiles, done, total, index, files.Count, path));
                continue;
            }

            if (!File.Exists(source))
            {
                throw SanovaException.Create(ErrorCodes.BaseSourceMissing, $"Original game file missing: {path}", ("path", path));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + ".sanova-copy";
            string sha;
            long length;
            try
            {
                var baseline = done;
                await using (var input = FileHashing.OpenSequentialRead(source))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, FileHashing.BufferSize, FileOptions.Asynchronous))
                {
                    var copied = 0L;
                    var reporter = new InlineProgress<long>(bytes =>
                    {
                        copied += bytes;
                        progress?.Report(new OperationProgress(OperationStage.CopyingGameFiles, baseline + copied, total, index, files.Count, path));
                    });
                    (sha, length) = await FileHashing.CopyAndHashAsync(input, output, null, reporter, cancellationToken).ConfigureAwait(false);
                }
                File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
                FileSystem.MoveFile(temp, target, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                FileSystem.TryDeleteFile(temp);
                SaveCheckpoint(checkpointPath, records);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileSystem.TryDeleteFile(temp);
                SaveCheckpoint(checkpointPath, records);
                throw FileSystem.MapIoException(ex, target);
            }

            if (expectedSha is not null && !FileHashing.HashEquals(expectedSha, sha) || length != expectedSize)
            {
                mismatches.Add(path);
            }

            records[path] = new InstalledFile
            {
                Size = length,
                Sha256 = sha,
                Origin = FileOrigin.Base,
                LastWriteUtc = File.GetLastWriteTimeUtc(target),
            };
            done += length;

            if (checkpointClock.ElapsedMilliseconds > 1000)
            {
                SaveCheckpoint(checkpointPath, records);
                checkpointClock.Restart();
            }
        }

        FileSystem.TryDeleteFile(checkpointPath);
        if (mismatches.Count > 0)
        {
            _logger.LogWarning("{Count} copied file(s) differ from the reference list (first: {First})", mismatches.Count, mismatches[0]);
        }
        _logger.LogInformation("Copied {Count} original game file(s), {Bytes} bytes", records.Count, done);
        return new BaseCopyResult(records, mismatches);
    }

    private static Dictionary<string, InstalledFile> LoadCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(SafePath.Comparer);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, InstalledFile>>(File.ReadAllBytes(path), JsonDefaults.Compact);
            return new Dictionary<string, InstalledFile>(loaded ?? [], SafePath.Comparer);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(SafePath.Comparer);
        }
    }

    private static void SaveCheckpoint(string path, Dictionary<string, InstalledFile> records)
    {
        try
        {
            AtomicFile.WriteJson(path, records, JsonDefaults.Compact);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a checkpoint only means re-copying some files later.
        }
    }
}
