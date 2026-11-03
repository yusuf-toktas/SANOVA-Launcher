using System.IO.Compression;
using System.Net;
using Sanova.Core.Downloads;

namespace Sanova.Core.Tests;

public class ResumableDownloaderTests
{
    private static readonly RetryPolicy Fast = new() { MaxAttempts = 4, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) };

    private static ResumableDownloader Create(FakeHttpServer server) => new(server.CreateClient(), new BandwidthThrottle(), Log.For<ResumableDownloader>(), Fast);

    [Fact]
    public async Task Downloads_and_verifies()
    {
        using var temp = new TempDir();
        var server = new FakeHttpServer();
        var data = Bytes.Random(300_000, 1);
        server.Files["/pkg.zip"] = data;
        var destination = Path.Combine(temp.Path, "pkg.zip");
        long reported = 0;

        await Create(server).DownloadAsync(new DownloadRequest([new Uri("https://cdn.test/pkg.zip")], destination, data.Length, Bytes.Sha(data)),
            new InlineProgress<long>(d => reported += d), TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(data.Length, reported);
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Resumes_after_a_dropped_connection()
    {
        using var temp = new TempDir();
        var server = new FakeHttpServer();
        var data = Bytes.Random(1_000_000, 2);
        server.Files["/big.zip"] = data;
        server.TruncateOnce["/big.zip"] = 400_000;
        var destination = Path.Combine(temp.Path, "big.zip");

        await Create(server).DownloadAsync(new DownloadRequest([new Uri("https://cdn.test/big.zip")], destination, data.Length, Bytes.Sha(data)), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(Bytes.Sha(data), Bytes.Sha(await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken)));
        // The retry continues exactly where the connection dropped instead of starting over.
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(400_000, server.Requests[1].Range?.Ranges.First().From);
    }

    [Fact]
    public async Task Restarts_when_the_server_ignores_ranges()
    {
        using var temp = new TempDir();
        var server = new FakeHttpServer { SupportsRanges = false };
        var data = Bytes.Random(200_000, 3);
        server.Files["/f.bin"] = data;
        server.TruncateOnce["/f.bin"] = 50_000;
        var destination = Path.Combine(temp.Path, "f.bin");

        await Create(server).DownloadAsync(new DownloadRequest([new Uri("https://cdn.test/f.bin")], destination, data.Length, Bytes.Sha(data)), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_corrupted_content_and_leaves_nothing_behind()
    {
        using var temp = new TempDir();
        var server = new FakeHttpServer();
        var data = Bytes.Random(10_000, 4);
        server.Files["/f.bin"] = data;
        var destination = Path.Combine(temp.Path, "f.bin");

        var ex = await Assert.ThrowsAsync<SanovaException>(() => Create(server).DownloadAsync(
            new DownloadRequest([new Uri("https://cdn.test/f.bin")], destination, data.Length, new string('0', 64)), null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.DownloadHashMismatch, ex.Code);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Falls_back_to_mirrors_and_retries_transient_errors()
    {
        using var temp = new TempDir();
        var server = new FakeHttpServer();
        var data = Bytes.Random(5_000, 5);
        server.Files["/mirror/f.bin"] = data;
        server.Failures["/mirror/f.bin"] = (2, HttpStatusCode.ServiceUnavailable);
        var destination = Path.Combine(temp.Path, "f.bin");

        await Create(server).DownloadAsync(new DownloadRequest(
            [new Uri("https://primary.test/missing/f.bin"), new Uri("https://primary.test/mirror/f.bin")], destination, data.Length, Bytes.Sha(data)),
            null, TestContext.Current.CancellationToken);

        Assert.Equal(data, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        Assert.Equal(1, server.Requests.Count(r => r.Path == "/missing/f.bin"));
        Assert.Equal(3, server.Requests.Count(r => r.Path == "/mirror/f.bin"));
    }

    [Fact]
    public async Task Copies_local_file_sources_with_verification()
    {
        using var temp = new TempDir();
        var data = Bytes.Random(70_000, 6);
        var source = temp.File("source/pkg.zip", data);
        var destination = Path.Combine(temp.Path, "out", "pkg.zip");
        await Create(new FakeHttpServer()).DownloadAsync(new DownloadRequest([new Uri(source)], destination, data.Length, Bytes.Sha(data)), null,
            TestContext.Current.CancellationToken);
        Assert.Equal(data, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
    }
}

public class HttpRangeReadStreamTests
{
    private static byte[] BuildZip(out byte[] small, out byte[] large)
    {
        small = System.Text.Encoding.UTF8.GetBytes("handling data for one vehicle");
        large = Bytes.Random(3_000_000, 9);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = archive.CreateEntry("big/texture.txd", CompressionLevel.NoCompression).Open()) stream.Write(large);
            using (var stream = archive.CreateEntry("data/handling.cfg").Open()) stream.Write(small);
        }
        return memory.ToArray();
    }

    [Fact]
    public void Reads_a_single_entry_without_downloading_the_whole_archive()
    {
        var zip = BuildZip(out var small, out _);
        var server = new FakeHttpServer();
        server.Files["/pkg.zip"] = zip;

        using var stream = HttpRangeReadStream.Open(server.CreateClient(), new Uri("https://cdn.test/pkg.zip"), null, TestContext.Current.CancellationToken);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var entry = archive.GetEntry("data/handling.cfg")!.Open();
        using var copy = new MemoryStream();
        entry.CopyTo(copy);

        Assert.Equal(small, copy.ToArray());
        Assert.True(stream.BytesFetched < zip.Length / 4, $"fetched {stream.BytesFetched} of {zip.Length}");
    }

    [Fact]
    public void Detects_servers_without_range_support()
    {
        var server = new FakeHttpServer { SupportsRanges = false };
        server.Files["/pkg.zip"] = BuildZip(out _, out _);
        Assert.Throws<RangeNotSupportedException>(() =>
            HttpRangeReadStream.Open(server.CreateClient(), new Uri("https://cdn.test/pkg.zip"), null, TestContext.Current.CancellationToken));
    }
}

public class BandwidthThrottleTests
{
    [Fact]
    public async Task Unlimited_by_default_and_limits_when_configured()
    {
        var throttle = new BandwidthThrottle();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await throttle.WaitAsync(10_000_000, TestContext.Current.CancellationToken);
        Assert.True(clock.ElapsedMilliseconds < 100);

        throttle.BytesPerSecond = 100_000;
        clock.Restart();
        await throttle.WaitAsync(150_000, TestContext.Current.CancellationToken);
        Assert.True(clock.ElapsedMilliseconds >= 400, $"took {clock.ElapsedMilliseconds} ms");
    }
}
