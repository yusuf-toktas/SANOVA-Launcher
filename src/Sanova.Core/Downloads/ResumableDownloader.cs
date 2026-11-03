using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;

namespace Sanova.Core.Downloads;

public sealed record DownloadRequest(IReadOnlyList<Uri> Sources, string Destination, long? ExpectedSize, string? ExpectedSha256);

/// <summary>
/// Downloads a file with resume support (HTTP ranges + If-Range validators), mirror
/// fallback, retries, a shared speed limit, stall detection and mandatory size/SHA-256
/// verification. The destination only ever appears once it has been verified.
/// Progress is reported as byte deltas (negative when a broken partial file is discarded).
/// </summary>
public sealed class ResumableDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;
    private readonly BandwidthThrottle _throttle;
    private readonly RetryPolicy _retry;
    private readonly ILogger<ResumableDownloader> _logger;

    private sealed class PartMetadata
    {
        public string? Url { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public string? ExpectedSha256 { get; set; }
    }

    private sealed class Tracker(IProgress<long>? progress)
    {
        public long OnDisk { get; private set; }

        public void Set(long value)
        {
            var delta = value - OnDisk;
            OnDisk = value;
            if (delta != 0) progress?.Report(delta);
        }

        public void Add(long bytes) => Set(OnDisk + bytes);
    }

    public ResumableDownloader(HttpClient http, BandwidthThrottle throttle, ILogger<ResumableDownloader> logger, RetryPolicy? retry = null)
    {
        _http = http;
        _throttle = throttle;
        _logger = logger;
        _retry = retry ?? RetryPolicy.Default;
    }

    public async Task DownloadAsync(DownloadRequest request, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (request.Sources.Count == 0)
        {
            throw SanovaException.Create(ErrorCodes.DownloadFailed, "No download source available.", ("url", "-"), ("detail", "no source"));
        }

        var tracker = new Tracker(progress);
        if (await IsAlreadyCompleteAsync(request, cancellationToken).ConfigureAwait(false))
        {
            tracker.Set(new FileInfo(request.Destination).Length);
            return;
        }

        Exception? last = null;
        foreach (var source in request.Sources)
        {
            for (var attempt = 1; attempt <= _retry.MaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await DownloadOnceAsync(source, request, tracker, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not SanovaException && RetryPolicy.IsTransient(ex, cancellationToken))
                {
                    last = ex;
                    if (attempt == _retry.MaxAttempts) break;
                    var delay = _retry.GetDelay(attempt, (ex as HttpStatusException)?.RetryAfter);
                    _logger.LogWarning("Download attempt {Attempt} of {Url} failed ({Error}); retrying in {Delay:N1}s",
                        attempt, Redact(source), ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpStatusException or HttpRequestException or SanovaException { Code: ErrorCodes.DownloadHashMismatch or ErrorCodes.DownloadSizeMismatch })
                {
                    // Not worth retrying against this source; try the next mirror.
                    last = ex;
                    _logger.LogWarning("Download of {Url} failed: {Error}", Redact(source), ex.Message);
                    break;
                }
            }
        }

        if (last is SanovaException sanova) throw sanova;
        if (last is not null && RetryPolicy.IsOffline(last))
        {
            throw SanovaException.Wrap(ErrorCodes.SourceUnreachable, $"Network unavailable: {last.Message}", last, ("detail", last.Message));
        }
        throw SanovaException.Wrap(ErrorCodes.DownloadFailed, $"Download failed: {last?.Message}", last ?? new InvalidOperationException(),
            ("url", Redact(request.Sources[0])), ("detail", last?.Message));
    }

    private static async Task<bool> IsAlreadyCompleteAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        var info = new FileInfo(request.Destination);
        if (!info.Exists) return false;
        if (request.ExpectedSize is { } size && info.Length != size) return false;
        if (request.ExpectedSha256 is null) return request.ExpectedSize is not null;
        var actual = await FileHashing.Sha256FileAsync(request.Destination, null, cancellationToken).ConfigureAwait(false);
        return FileHashing.HashEquals(actual, request.ExpectedSha256);
    }

    private async Task DownloadOnceAsync(Uri source, DownloadRequest request, Tracker tracker, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Destination))!);
        var part = request.Destination + ".part";
        var metaPath = part + ".json";
        var meta = ReadMetadata(metaPath);

        long existing = 0;
        if (File.Exists(part))
        {
            existing = new FileInfo(part).Length;
            var usable = meta is not null
                         && string.Equals(meta.ExpectedSha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                         && (request.ExpectedSize is null || existing <= request.ExpectedSize);
            if (!usable)
            {
                DiscardPart(part, metaPath);
                existing = 0;
                meta = null;
            }
        }
        tracker.Set(existing);

        var complete = request.ExpectedSize is { } expectedSize && existing == expectedSize;
        if (!complete)
        {
            if (source.IsFile)
            {
                await CopyLocalAsync(source.LocalPath, part, existing, tracker, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await TransferAsync(source, request, part, metaPath, meta, existing, tracker, cancellationToken).ConfigureAwait(false);
            }
        }

        var length = new FileInfo(part).Length;
        if (request.ExpectedSize is { } size && length != size)
        {
            DiscardPart(part, metaPath);
            tracker.Set(0);
            throw new RestartDownloadException($"Downloaded {length} bytes, expected {size}.");
        }

        if (request.ExpectedSha256 is { } expectedHash)
        {
            var actual = await FileHashing.Sha256FileAsync(part, null, cancellationToken).ConfigureAwait(false);
            if (!FileHashing.HashEquals(actual, expectedHash))
            {
                DiscardPart(part, metaPath);
                tracker.Set(0);
                if (existing > 0)
                {
                    // A resumed file can be corrupted by a changed server copy; one clean retry is worthwhile.
                    throw new RestartDownloadException("Checksum mismatch after resuming.");
                }
                throw SanovaException.Create(ErrorCodes.DownloadHashMismatch, $"Checksum mismatch for {Redact(source)}: expected {expectedHash}, got {actual}.",
                    ("url", Redact(source)), ("file", Path.GetFileName(request.Destination)));
            }
        }

        FileSystem.MoveFile(part, request.Destination, overwrite: true);
        FileSystem.TryDeleteFile(metaPath);
    }

    private async Task TransferAsync(Uri source, DownloadRequest request, string part, string metaPath, PartMetadata? meta, long existing,
        Tracker tracker, CancellationToken cancellationToken)
    {
        if (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp)
        {
            throw SanovaException.Create(ErrorCodes.SourceInsecure, $"Unsupported URL scheme: {source.Scheme}", ("url", Redact(source)));
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, source);
        if (existing > 0)
        {
            message.Headers.Range = new RangeHeaderValue(existing, null);
            if (meta?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
            {
                message.Headers.IfRange = new RangeConditionHeaderValue(tag);
            }
            else if (meta?.LastModified is { } lastModified)
            {
                message.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            }
        }

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            DiscardPart(part, metaPath);
            tracker.Set(0);
            throw new RestartDownloadException("Server rejected the resume range.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw HttpStatusException.From(response, source);
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != existing)
        {
            DiscardPart(part, metaPath);
            tracker.Set(0);
            throw new RestartDownloadException("Server returned an unexpected range.");
        }
        if (!append)
        {
            tracker.Set(0);
        }

        var total = (append ? existing : 0) + (response.Content.Headers.ContentLength ?? 0);
        if (request.ExpectedSize is { } expected && response.Content.Headers.ContentLength is not null && total != expected)
        {
            throw SanovaException.Create(ErrorCodes.DownloadSizeMismatch, $"Server reports {total} bytes, manifest expects {expected}.",
                ("url", Redact(source)), ("file", Path.GetFileName(request.Destination)));
        }

        WriteMetadata(metaPath, new PartMetadata
        {
            Url = source.ToString(),
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified,
            ExpectedSha256 = request.ExpectedSha256,
        });

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = OpenPart(part, append);
        await CopyAsync(input, output, request.ExpectedSize, tracker, throttled: true, part, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyLocalAsync(string sourcePath, string part, long existing, Tracker tracker, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw SanovaException.Create(ErrorCodes.DownloadFailed, $"Local source missing: {sourcePath}", ("url", sourcePath), ("detail", "file not found"));
        }
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileHashing.BufferSize, FileOptions.Asynchronous);
        if (existing > input.Length)
        {
            existing = 0;
            tracker.Set(0);
        }
        input.Position = existing;
        await using var output = OpenPart(part, append: existing > 0);
        await CopyAsync(input, output, null, tracker, throttled: false, part, cancellationToken).ConfigureAwait(false);
    }

    private static FileStream OpenPart(string part, bool append)
        => new(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, FileHashing.BufferSize, FileOptions.Asynchronous);

    private async Task CopyAsync(Stream input, Stream output, long? expectedSize, Tracker tracker, bool throttled, string part, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The download stalled.");
                }
                if (read == 0) break;

                if (throttled) await _throttle.WaitAsync(read, cancellationToken).ConfigureAwait(false);

                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw FileSystem.MapIoException(ex, part);
                }

                tracker.Add(read);
                if (expectedSize is { } limit && tracker.OnDisk > limit)
                {
                    throw new RestartDownloadException("Received more data than expected.");
                }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static PartMetadata? ReadMetadata(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PartMetadata>(File.ReadAllBytes(path), JsonDefaults.Compact) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteMetadata(string path, PartMetadata meta)
    {
        try
        {
            AtomicFile.WriteJson(path, meta, JsonDefaults.Compact);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resume metadata is an optimization; the download itself can proceed.
        }
    }

    private static void DiscardPart(string part, string metaPath)
    {
        FileSystem.TryDeleteFile(part);
        FileSystem.TryDeleteFile(metaPath);
    }

    /// <summary>Strips query strings (signed URLs may carry tokens) before logging.</summary>
    public static string Redact(Uri uri) => uri.IsFile ? uri.LocalPath : uri.GetLeftPart(UriPartial.Path);
}
