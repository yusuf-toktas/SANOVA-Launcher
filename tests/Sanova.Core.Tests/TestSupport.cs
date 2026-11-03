using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sanova.Core.Tests;

/// <summary>A unique temporary directory, deleted when disposed. Tests never touch real game folders.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sanova-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var full = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(full);
        return full;
    }

    public string File(string relative, string content) => File(relative, System.Text.Encoding.UTF8.GetBytes(content));

    public string File(string relative, byte[] content) => WriteFile(Path, relative, content);

    public static string WriteFile(string root, string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class Bytes
{
    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>
/// In-memory HTTP server for download tests: byte ranges, redirects, failures and truncated
/// transfers, without opening sockets.
/// </summary>
public sealed class FakeHttpServer : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public bool SupportsRanges { get; set; } = true;

    /// <summary>Path → number of upcoming requests that fail with this status code.</summary>
    public Dictionary<string, (int Remaining, HttpStatusCode Status)> Failures { get; } = new();

    /// <summary>Path → bytes after which the next response body breaks (simulated connection drop).</summary>
    public Dictionary<string, long> TruncateOnce { get; } = new();

    public List<(string Path, RangeHeaderValue? Range)> Requests { get; } = [];

    public long BytesServed { get; private set; }

    /// <summary>Optional hook that fully answers a request (e.g. rate-limit responses with headers).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public HttpClient CreateClient() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add((path, request.Headers.Range));

        if (Override?.Invoke(request) is { } overridden)
        {
            overridden.RequestMessage = request;
            return Task.FromResult(overridden);
        }

        if (Failures.TryGetValue(path, out var failure) && failure.Remaining > 0)
        {
            Failures[path] = (failure.Remaining - 1, failure.Status);
            return Task.FromResult(new HttpResponseMessage(failure.Status) { RequestMessage = request });
        }

        if (!Files.TryGetValue(path, out var data))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        var etag = new EntityTagHeaderValue($"\"{Bytes.Sha(data)[..16]}\"");
        if (request.Headers.IfNoneMatch.Any(t => t.Tag == etag.Tag))
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
            notModified.Headers.ETag = etag;
            return Task.FromResult(notModified);
        }

        long start = 0;
        var end = data.LongLength - 1;
        var partial = false;
        if (SupportsRanges && request.Headers.Range?.Ranges.FirstOrDefault() is { } range)
        {
            start = range.From ?? 0;
            end = Math.Min(range.To ?? end, data.LongLength - 1);
            if (start >= data.LongLength)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request });
            }
            partial = true;
        }

        var slice = data.AsSpan((int)start, (int)(end - start + 1)).ToArray();
        Stream body = new MemoryStream(slice);
        if (TruncateOnce.Remove(path, out var limit)) body = new BreakingStream(slice, limit);
        BytesServed += slice.Length;

        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(body),
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Headers.ETag = new EntityTagHeaderValue($"\"{Bytes.Sha(data)[..16]}\"");
        if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, data.LongLength);
        return Task.FromResult(response);
    }

    private sealed class BreakingStream(byte[] data, long breakAfter) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= breakAfter) throw new IOException("Simulated connection drop.");
            var available = (int)Math.Min(count, Math.Min(data.Length - _position, breakAfter - _position));
            if (available <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public static class Log
{
    public static NullLogger<T> For<T>() => NullLogger<T>.Instance;
}

/// <summary>
/// Optional real GTA San Andreas v1.0 installation for the read-only checks, configured with
/// the SANOVA_REAL_GAME_PATH environment variable. Null when it isn't set or has no game.
/// </summary>
public static class RealGame
{
    public const string PathVariable = "SANOVA_REAL_GAME_PATH";

    public static string? Location
        => Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } path && File.Exists(Path.Combine(path, "gta_sa.exe")) ? path : null;

    public static string SkipReason => $"Set {PathVariable} to an original GTA San Andreas v1.0 folder to run this check.";
}
