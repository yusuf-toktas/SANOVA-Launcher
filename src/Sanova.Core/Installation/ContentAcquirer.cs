using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Sanova.Core.Content;
using Sanova.Core.Downloads;
using Sanova.Core.IO;
using Sanova.Core.Updates;

namespace Sanova.Core.Installation;

public sealed class AcquisitionRequest
{
    public required ResolvedManifest Manifest { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }

    /// <summary>Absolute staging folder; each file lands at &lt;staging&gt;/&lt;path&gt;.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>Absolute package cache folder (resumable downloads).</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Component id → archive the player downloaded manually.</summary>
    public IReadOnlyDictionary<string, string> ManualArchives { get; init; } = new Dictionary<string, string>();

    public int MaxParallelDownloads { get; init; } = 3;
}

/// <summary>
/// Produces verified copies of manifest files in a staging folder. Per archive it picks the
/// cheapest safe strategy: a cached or local copy, a full (resumable) download, or — when
/// only a small part of a large package is needed — remote extraction through HTTP ranges.
/// Every extracted file is size-limited and SHA-256 verified before it counts as staged.
/// </summary>
public sealed class ContentAcquirer
{
    private const long SmallPackageThreshold = 16L * 1024 * 1024;
    private const double FullDownloadRatio = 0.5;

    private readonly ResumableDownloader _downloader;
    private readonly HttpClient _downloadHttp;
    private readonly BandwidthThrottle _throttle;
    private readonly ILogger<ContentAcquirer> _logger;

    private enum Strategy
    {
        Cached,
        LocalFile,
        FullDownload,
        RemotePartial,
    }

    private sealed class ArchiveJob
    {
        public required string Label { get; init; }
        public required IReadOnlyList<ManifestFile> Files { get; init; }
        public required IReadOnlyList<Uri> Uris { get; init; }
        public required string CachePath { get; init; }
        public long? Size { get; init; }
        public string? Sha256 { get; init; }
        public string? LocalPath { get; set; }
        public Strategy Strategy { get; set; }
    }

    public ContentAcquirer(ResumableDownloader downloader, HttpClient downloadHttp, BandwidthThrottle throttle, ILogger<ContentAcquirer> logger)
    {
        _downloader = downloader;
        _downloadHttp = downloadHttp;
        _throttle = throttle;
        _logger = logger;
    }

    /// <summary>Components with a Manual source whose archive the player still has to provide.</summary>
    public static IReadOnlyList<ComponentSpec> MissingManualArchives(ContentManifest manifest, IEnumerable<ManifestFile> files, IReadOnlyDictionary<string, string> provided)
        => files.Where(f => f.Package is null)
            .Select(f => f.Component)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(manifest.FindComponent)
            .OfType<ComponentSpec>()
            .Where(c => c.SourceKind == ComponentSourceKind.Manual && !provided.ContainsKey(c.Id))
            .ToList();

    public async Task AcquireAsync(AcquisitionRequest request, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (request.Files.Count == 0) return;
        var jobs = PlanJobs(request);

        // Phase 1: whole-archive downloads, in parallel.
        var downloads = jobs.Where(j => j.Strategy == Strategy.FullDownload).ToList();
        if (downloads.Count > 0)
        {
            await DownloadAllAsync(downloads, request.MaxParallelDownloads, progress, cancellationToken).ConfigureAwait(false);
        }

        // Phase 2: extraction. Archives are read sequentially (zip readers are not thread-safe).
        var totalBytes = request.Files.Sum(f => f.Size);
        long extracted = 0;
        var filesDone = 0;
        void OnBytes(long count)
        {
            var done = Interlocked.Add(ref extracted, count);
            progress?.Report(new OperationProgress(OperationStage.Extracting, done, totalBytes, Volatile.Read(ref filesDone), request.Files.Count));
        }

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => ExtractJob(job, request, OnBytes, () => Interlocked.Increment(ref filesDone), progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private List<ArchiveJob> PlanJobs(AcquisitionRequest request)
    {
        var manifest = request.Manifest.Manifest;
        var jobs = new List<ArchiveJob>();

        foreach (var group in request.Files.Where(f => f.Package is not null).GroupBy(f => f.Package!, StringComparer.OrdinalIgnoreCase))
        {
            var package = manifest.FindPackage(group.Key)!;
            var job = new ArchiveJob
            {
                Label = package.FileName,
                Files = group.ToList(),
                Uris = request.Manifest.GetPackageUris(package),
                CachePath = Path.Combine(request.CacheDirectory, "packages", package.Sha256.ToLowerInvariant() + ".zip"),
                Size = package.Size,
                Sha256 = package.Sha256,
            };
            var needed = job.Files.Sum(f => f.PackedSize ?? f.Size);
            job.Strategy = ChooseStrategy(job, package.Size, needed);
            jobs.Add(job);
        }

        foreach (var group in request.Files.Where(f => f.Package is null).GroupBy(f => f.Component, StringComparer.OrdinalIgnoreCase))
        {
            var component = manifest.FindComponent(group.Key)!;
            var source = component.Source!;
            var job = new ArchiveJob
            {
                Label = component.Name.Default,
                Files = group.ToList(),
                Uris = source.Type == ComponentSourceKind.External && Uri.TryCreate(source.Url, UriKind.Absolute, out var url) ? [url] : [],
                CachePath = Path.Combine(request.CacheDirectory, "external", source.Sha256!.ToLowerInvariant() + ".zip"),
                Size = source.Size,
                Sha256 = source.Sha256,
            };

            if (source.Type == ComponentSourceKind.Manual)
            {
                if (!request.ManualArchives.TryGetValue(component.Id, out var localArchive) || !File.Exists(localArchive))
                {
                    throw SanovaException.Create(ErrorCodes.ManualDownloadRequired, $"Component '{component.Id}' needs a manually downloaded archive.",
                        ("component", component.Name.Default), ("page", source.PageUrl));
                }
                job.LocalPath = localArchive;
                job.Strategy = Strategy.LocalFile;
            }
            else
            {
                job.Strategy = File.Exists(job.CachePath) ? Strategy.Cached : Strategy.FullDownload;
            }
            jobs.Add(job);
        }
        return jobs;
    }

    private static Strategy ChooseStrategy(ArchiveJob job, long packageSize, long neededBytes)
    {
        if (File.Exists(job.CachePath) && new FileInfo(job.CachePath).Length == packageSize) return Strategy.Cached;
        var local = job.Uris.FirstOrDefault(u => u.IsFile);
        if (local is not null)
        {
            job.LocalPath = local.LocalPath;
            return Strategy.LocalFile;
        }
        if (job.Uris.Count == 0)
        {
            throw SanovaException.Create(ErrorCodes.DownloadFailed, $"No download location for {job.Label}.", ("url", job.Label), ("detail", "no source"));
        }
        return packageSize <= SmallPackageThreshold || neededBytes >= packageSize * FullDownloadRatio
            ? Strategy.FullDownload
            : Strategy.RemotePartial;
    }

    private async Task DownloadAllAsync(IReadOnlyList<ArchiveJob> jobs, int parallelism, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var total = jobs.Sum(j => j.Size ?? 0);
        long done = 0;
        var aggregate = new InlineProgress<long>(delta =>
        {
            var now = Interlocked.Add(ref done, delta);
            progress?.Report(new OperationProgress(OperationStage.Downloading, Math.Max(0, now), total, CurrentItem: null));
        });

        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (job, token) =>
            {
                _logger.LogInformation("Downloading {Archive} ({Size} bytes)", job.Label, job.Size);
                await _downloader.DownloadAsync(new DownloadRequest(job.Uris, job.CachePath, job.Size, job.Sha256), aggregate, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private void ExtractJob(ArchiveJob job, AcquisitionRequest request, Action<long> onBytes, Action onFile, IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        switch (job.Strategy)
        {
            case Strategy.LocalFile:
                if (job.Sha256 is not null && job.Files.Count > 0 && job.Files[0].Package is null)
                {
                    // Manually supplied archives must be exactly the published file.
                    var actual = FileHashing.Sha256FileAsync(job.LocalPath!, null, cancellationToken).GetAwaiter().GetResult();
                    if (!FileHashing.HashEquals(actual, job.Sha256))
                    {
                        throw SanovaException.Create(ErrorCodes.DownloadHashMismatch, $"Archive for {job.Label} does not match the expected file.",
                            ("url", job.LocalPath), ("file", Path.GetFileName(job.LocalPath)));
                    }
                }
                ExtractFromFile(job.LocalPath!, job, request.StagingDirectory, onBytes, onFile, cancellationToken);
                return;

            case Strategy.Cached:
            case Strategy.FullDownload:
                try
                {
                    ExtractFromFile(job.CachePath, job, request.StagingDirectory, onBytes, onFile, cancellationToken);
                }
                catch (SanovaException ex) when (job.Strategy == Strategy.Cached && ex.Code is ErrorCodes.FileVerificationFailed or ErrorCodes.ArchiveInvalid)
                {
                    // A damaged cache entry: fetch a fresh copy once.
                    _logger.LogWarning(ex, "Cached archive {Archive} is damaged; downloading again", job.Label);
                    FileSystem.DeleteFile(job.CachePath);
                    _downloader.DownloadAsync(new DownloadRequest(job.Uris, job.CachePath, job.Size, job.Sha256), null, cancellationToken).GetAwaiter().GetResult();
                    ExtractFromFile(job.CachePath, job, request.StagingDirectory, onBytes, onFile, cancellationToken);
                }
                return;

            case Strategy.RemotePartial:
                try
                {
                    ExtractRemote(job, request.StagingDirectory, onBytes, onFile, cancellationToken);
                }
                catch (RangeNotSupportedException)
                {
                    _logger.LogInformation("Server ignores byte ranges for {Archive}; downloading it whole", job.Label);
                    progress?.Report(new OperationProgress(OperationStage.Downloading, 0, job.Size ?? 0));
                    _downloader.DownloadAsync(new DownloadRequest(job.Uris, job.CachePath, job.Size, job.Sha256), null, cancellationToken).GetAwaiter().GetResult();
                    ExtractFromFile(job.CachePath, job, request.StagingDirectory, onBytes, onFile, cancellationToken);
                }
                return;
        }
    }

    private void ExtractRemote(ArchiveJob job, string stagingDirectory, Action<long> onBytes, Action onFile, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var uri in job.Uris.Where(u => !u.IsFile))
        {
            try
            {
                using var stream = HttpRangeReadStream.Open(_downloadHttp, uri, _throttle, cancellationToken);
                if (job.Size is { } size && stream.Length != size)
                {
                    throw SanovaException.Create(ErrorCodes.DownloadSizeMismatch, $"Remote archive is {stream.Length} bytes, expected {size}.",
                        ("url", ResumableDownloader.Redact(uri)), ("file", job.Label));
                }
                Extract(stream, job, stagingDirectory, onBytes, onFile, cancellationToken);
                _logger.LogInformation("Extracted {Count} file(s) from {Archive} using {Bytes} bytes of range requests", job.Files.Count, job.Label, stream.BytesFetched);
                return;
            }
            catch (RangeNotSupportedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpStatusException or HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                _logger.LogWarning(ex, "Partial download from {Url} failed; trying the next source", ResumableDownloader.Redact(uri));
            }
        }
        throw SanovaException.Wrap(ErrorCodes.DownloadFailed, $"Could not read {job.Label}: {last?.Message}", last ?? new InvalidOperationException(),
            ("url", job.Label), ("detail", last?.Message));
    }

    private static void ExtractFromFile(string archivePath, ArchiveJob job, string stagingDirectory, Action<long> onBytes, Action onFile, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileHashing.BufferSize);
        Extract(stream, job, stagingDirectory, onBytes, onFile, cancellationToken);
    }

    private static void Extract(Stream archiveStream, ArchiveJob job, string stagingDirectory, Action<long> onBytes, Action onFile, CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw SanovaException.Wrap(ErrorCodes.ArchiveInvalid, $"{job.Label} is not a valid zip archive.", ex, ("file", job.Label));
        }

        using (archive)
        {
            var entries = new Dictionary<string, (ZipArchiveEntry Entry, int Index)>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.EndsWith('/')) entries.TryAdd(name, (entry, index));
                index++;
            }

            // Reading in archive order keeps remote access sequential.
            var ordered = job.Files
                .Select(f => (File: f, Found: entries.TryGetValue(SafePath.NormalizeRelative(f.EntryName), out var e), e.Entry, e.Index))
                .OrderBy(x => x.Found ? x.Index : int.MaxValue)
                .ToList();

            foreach (var (file, found, entry, _) in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!found)
                {
                    throw SanovaException.Create(ErrorCodes.ArchiveEntryMissing, $"'{file.EntryName}' is missing from {job.Label}.", ("file", file.Path), ("archive", job.Label));
                }
                if (entry.Length != file.Size)
                {
                    throw SanovaException.Create(ErrorCodes.FileVerificationFailed, $"'{file.EntryName}' in {job.Label} has an unexpected size.", ("file", file.Path));
                }

                var staged = SafePath.GetFullPathUnder(stagingDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                var temp = staged + ".partial";
                SafePath.RejectReparsePoints(temp);
                string sha;
                long length;
                try
                {
                    using var input = entry.Open();
                    using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, FileHashing.BufferSize);
                    (sha, length) = FileHashing.CopyAndHash(input, output, file.Size, onBytes, cancellationToken);
                }
                catch (InvalidDataException ex)
                {
                    FileSystem.TryDeleteFile(temp);
                    throw SanovaException.Wrap(ErrorCodes.ArchiveInvalid, $"'{file.EntryName}' in {job.Label} is corrupted.", ex, ("file", job.Label));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileSystem.TryDeleteFile(temp);
                    throw FileSystem.MapIoException(ex, staged);
                }

                if (length != file.Size || !FileHashing.HashEquals(sha, file.Sha256))
                {
                    FileSystem.TryDeleteFile(temp);
                    throw SanovaException.Create(ErrorCodes.FileVerificationFailed, $"'{file.Path}' failed verification (expected {file.Sha256}, got {sha}).", ("file", file.Path));
                }
                FileSystem.MoveFile(temp, staged, overwrite: true);
                onFile();
            }
        }
    }
}
