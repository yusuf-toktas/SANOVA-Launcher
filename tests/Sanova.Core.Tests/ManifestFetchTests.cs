using System.Net;
using Sanova.Core.Settings;
using Sanova.Core.Updates;

namespace Sanova.Core.Tests;

public class ManifestFetchTests
{
    [Fact]
    public async Task A_chunked_manifest_is_rejected_before_reading_an_unbounded_body()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        using var body = new OversizedStream();
        using var server = new FakeHttpServer
        {
            Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) },
        };
        using var http = server.CreateClient();
        var service = new ContentUpdateService(http, paths,
            new GitHubReleasesClient(http, paths.HttpCacheDirectory, Log.For<GitHubReleasesClient>()),
            Log.For<ContentUpdateService>());

        var result = await service.CheckAsync(new ContentSourceConfig
        {
            Type = ContentSourceType.Url,
            ManifestUrl = "https://content.example/sanova-manifest.json",
        }, new ManifestTrustConfig(), "stable", false, TestContext.Current.CancellationToken);

        Assert.Equal(ContentCheckStatus.Failed, result.Status);
        Assert.Equal(ErrorCodes.ManifestInvalid, result.Error?.Code);
        Assert.InRange(body.BytesRead, 1, ManifestLoader.MaxManifestBytes + 1L);
        Assert.False(Directory.Exists(paths.ManifestCacheDirectory));
    }

    private sealed class OversizedStream : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = (int)Math.Min(count, 2L * ManifestLoader.MaxManifestBytes - BytesRead);
            buffer.AsSpan(offset, length).Fill((byte)' ');
            BytesRead += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(buffer.Length, 2L * ManifestLoader.MaxManifestBytes - BytesRead);
            buffer.Span[..length].Fill((byte)' ');
            BytesRead += length;
            return ValueTask.FromResult(length);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
