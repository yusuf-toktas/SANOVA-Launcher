using System.Net;
using System.Net.Http.Headers;

namespace Sanova.Core.Downloads;

/// <summary>
/// A read-only, seekable stream over a remote file using HTTP range requests. Wrapping a
/// release zip in it lets <see cref="System.IO.Compression.ZipArchive"/> read the central
/// directory and only the entries an update needs, instead of downloading the whole package.
/// Sequential reads grow the request window (up to 8 MB) to keep the request count low.
/// </summary>
public sealed class HttpRangeReadStream : Stream
{
    private const int MinWindow = 256 * 1024;
    private const int MaxWindow = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _uri;
    private readonly BandwidthThrottle? _throttle;
    private readonly RetryPolicy _retry;
    private readonly CancellationToken _cancellationToken;
    private byte[] _buffer = [];
    private long _bufferStart = -1;
    private int _bufferLength;
    private long _position;
    private long _lastFetchEnd = -1;
    private int _window = MinWindow;

    private HttpRangeReadStream(HttpClient http, Uri uri, long length, BandwidthThrottle? throttle, RetryPolicy retry, CancellationToken cancellationToken)
    {
        _http = http;
        _uri = uri;
        Length = length;
        _throttle = throttle;
        _retry = retry;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Network bytes received so far (for progress reporting).</summary>
    public long BytesFetched { get; private set; }

    public event Action<long>? Fetched;

    /// <summary>Probes the server; throws <see cref="RangeNotSupportedException"/> when ranges are ignored.</summary>
    public static HttpRangeReadStream Open(HttpClient http, Uri uri, BandwidthThrottle? throttle, CancellationToken cancellationToken, RetryPolicy? retry = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw HttpStatusException.From(response, uri);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.Length is not { } length)
        {
            throw new RangeNotSupportedException(uri);
        }
        return new HttpRangeReadStream(http, uri, length, throttle, retry ?? RetryPolicy.Default, cancellationToken);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (_position >= Length || destination.IsEmpty) return 0;
        if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _bufferLength)
        {
            Fill(_position, destination.Length);
        }
        var offset = (int)(_position - _bufferStart);
        var count = Math.Min(destination.Length, _bufferLength - offset);
        _buffer.AsSpan(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    private void Fill(long start, int requested)
    {
        _window = start == _lastFetchEnd ? Math.Min(MaxWindow, _window * 2) : MinWindow;
        var size = Math.Max(_window, Math.Min(requested, MaxWindow));
        var end = Math.Min(Length - 1, start + size - 1);
        var expected = (int)(end - start + 1);

        for (var attempt = 1; ; attempt++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var data = FetchRange(start, end, expected);
                _buffer = data;
                _bufferStart = start;
                _bufferLength = expected;
                _lastFetchEnd = end + 1;
                return;
            }
            catch (Exception ex) when (RetryPolicy.IsTransient(ex, _cancellationToken) && attempt < _retry.MaxAttempts)
            {
                Thread.Sleep(_retry.GetDelay(attempt, (ex as HttpStatusException)?.RetryAfter));
            }
        }
    }

    private byte[] FetchRange(long start, long end, int expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken);
        if (!response.IsSuccessStatusCode) throw HttpStatusException.From(response, _uri);
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != start)
        {
            throw new RangeNotSupportedException(_uri);
        }

        var data = new byte[expected];
        using var body = response.Content.ReadAsStream(_cancellationToken);
        var filled = 0;
        while (filled < expected)
        {
            var read = body.Read(data, filled, Math.Min(expected - filled, 256 * 1024));
            if (read == 0) throw new IOException("Range response ended early.");
            _throttle?.WaitAsync(read, _cancellationToken).AsTask().GetAwaiter().GetResult();
            filled += read;
            BytesFetched += read;
            Fetched?.Invoke(read);
        }
        return data;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
