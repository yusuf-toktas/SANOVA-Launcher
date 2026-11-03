using System.Net;
using System.Text;
using System.Text.Json;
using Sanova.Core.Content;
using Sanova.Core.Settings;
using Sanova.Core.Updates;

namespace Sanova.Core.Tests;

public class GitHubContentSourceTests
{
    private static string ReleasesJson(params (string Tag, bool Prerelease, bool Draft, bool HasManifest)[] releases)
        => JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object?>
        {
            ["tag_name"] = r.Tag,
            ["name"] = "SANOVA " + r.Tag,
            ["body"] = "Notes for " + r.Tag,
            ["draft"] = r.Draft,
            ["prerelease"] = r.Prerelease,
            ["published_at"] = "2026-10-01T12:00:00Z",
            ["assets"] = r.HasManifest
                ? new object[]
                {
                    new Dictionary<string, object> { ["name"] = "sanova-manifest.json", ["size"] = 100, ["browser_download_url"] = $"https://github.com/o/r/releases/download/{r.Tag}/sanova-manifest.json" },
                    new Dictionary<string, object> { ["name"] = "sanova-manifest.json.sig", ["size"] = 90, ["browser_download_url"] = $"https://github.com/o/r/releases/download/{r.Tag}/sanova-manifest.json.sig" },
                    new Dictionary<string, object> { ["name"] = "sanova-core.zip", ["size"] = 10, ["browser_download_url"] = $"https://github.com/o/r/releases/download/{r.Tag}/sanova-core.zip" },
                }
                : Array.Empty<object>(),
        }));

    private static (GitHubContentSource Source, FakeHttpServer Server) Create(TempDir temp, string json)
    {
        var server = new FakeHttpServer();
        server.Files["/repos/o/r/releases"] = Encoding.UTF8.GetBytes(json);
        var client = new GitHubReleasesClient(server.CreateClient(), temp.Sub("cache"), Log.For<GitHubReleasesClient>());
        var config = new ContentSourceConfig { Repository = "o/r", Mirrors = ["https://mirror.example/sanova/"] };
        return (new GitHubContentSource(new GitHubRepository("o", "r"), config, client, Log.For<GitHubContentSource>()), server);
    }

    [Fact]
    public async Task Picks_the_newest_stable_release_with_a_manifest()
    {
        using var temp = new TempDir();
        var (source, _) = Create(temp, ReleasesJson(
            ("v1.3.0-beta.1", true, false, true),
            ("v1.2.0", false, false, true),
            ("v1.10.0", false, true, true),
            ("v1.2.5", false, false, false),
            ("v1.1.0", false, false, true)));

        var release = await source.GetLatestAsync("stable", TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release!.Version);
        Assert.Equal("https://github.com/o/r/releases/download/v1.2.0/sanova-manifest.json", release.ManifestUri.ToString());
        Assert.NotNull(release.SignatureUri);
        var uris = release.ResolvePackage(new PackageSpec { Id = "core", FileName = "sanova-core.zip", Size = 10, Sha256 = new string('a', 64) });
        Assert.Equal("https://github.com/o/r/releases/download/v1.2.0/sanova-core.zip", uris[0].ToString());
        Assert.Equal("https://mirror.example/sanova/sanova-core.zip", uris[^1].ToString());
    }

    [Fact]
    public async Task The_beta_channel_includes_prereleases()
    {
        using var temp = new TempDir();
        var (source, _) = Create(temp, ReleasesJson(("v1.2.0", false, false, true), ("v1.3.0-beta.1", true, false, true)));
        Assert.Equal("1.3.0-beta.1", (await source.GetLatestAsync("beta", TestContext.Current.CancellationToken))!.Version);
    }

    [Fact]
    public async Task Repeated_checks_use_conditional_requests()
    {
        using var temp = new TempDir();
        var (source, server) = Create(temp, ReleasesJson(("v1.0.0", false, false, true)));
        await source.GetLatestAsync("stable", TestContext.Current.CancellationToken);
        var second = await source.GetLatestAsync("stable", TestContext.Current.CancellationToken);
        Assert.Equal("1.0.0", second!.Version);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Reports_rate_limits_and_missing_repositories_clearly()
    {
        using var temp = new TempDir();
        var (source, server) = Create(temp, "[]");
        server.Override = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1900000000");
            return response;
        };
        var limited = await Assert.ThrowsAsync<SanovaException>(() => source.GetLatestAsync("stable", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SourceRateLimited, limited.Code);

        server.Override = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var missing = await Assert.ThrowsAsync<SanovaException>(() => source.GetLatestAsync("stable", TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.SourceNoRelease, missing.Code);
    }

    [Theory]
    [InlineData("owner/repo", true)]
    [InlineData("https://github.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("", false)]
    [InlineData("owner", false)]
    [InlineData("https://gitlab.com/owner/repo", false)]
    [InlineData("owner/repo/extra", false)]
    public void Parses_repository_references(string text, bool valid)
        => Assert.Equal(valid, GitHubRepository.TryParse(text, out _));
}

public class ContentUpdateServiceTests
{
    [Fact]
    public async Task Not_configured_sources_are_reported_as_such()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        var service = new ContentUpdateService(new HttpClient(), paths, new GitHubReleasesClient(new HttpClient(), paths.HttpCacheDirectory, Log.For<GitHubReleasesClient>()),
            Log.For<ContentUpdateService>());
        var result = await service.CheckAsync(new ContentSourceConfig(), new ManifestTrustConfig(), "stable", false, TestContext.Current.CancellationToken);
        Assert.Equal(ContentCheckStatus.NotConfigured, result.Status);
    }

    [Fact]
    public async Task Local_releases_are_verified_cached_and_reloaded()
    {
        using var fx = new ContentFixture();
        await fx.BuildAsync("1.0.0");
        var paths = new AppPaths(fx.Temp.Sub("data"));
        var service = new ContentUpdateService(new HttpClient(), paths, new GitHubReleasesClient(new HttpClient(), paths.HttpCacheDirectory, Log.For<GitHubReleasesClient>()),
            Log.For<ContentUpdateService>());
        var config = new ContentSourceConfig { Type = ContentSourceType.LocalFolder, LocalFolder = Path.Combine(fx.Temp.Path, "release-1.0.0") };

        var result = await service.CheckAsync(config, new ManifestTrustConfig(), "stable", false, TestContext.Current.CancellationToken);

        Assert.Equal(ContentCheckStatus.Available, result.Status);
        Assert.Equal("1.0.0", result.Manifest!.Manifest.ContentVersion);
        var cached = ResolvedManifest.TryLoad(paths.ManifestCacheDirectory);
        Assert.NotNull(cached);
        Assert.Equal(result.Manifest.Sha256, cached!.Sha256);
        Assert.NotEmpty(cached.GetPackageUris(cached.Manifest.Packages[0]));
    }

    [Fact]
    public async Task Offline_checks_report_offline_and_keep_the_last_verified_manifest()
    {
        using var fx = new ContentFixture();
        await fx.BuildAsync("1.0.0");
        var paths = new AppPaths(fx.Temp.Sub("data"));
        var online = new ContentUpdateService(new HttpClient(), paths, new GitHubReleasesClient(new HttpClient(), paths.HttpCacheDirectory, Log.For<GitHubReleasesClient>()),
            Log.For<ContentUpdateService>());
        var local = new ContentSourceConfig { Type = ContentSourceType.LocalFolder, LocalFolder = Path.Combine(fx.Temp.Path, "release-1.0.0") };
        Assert.Equal(ContentCheckStatus.Available, (await online.CheckAsync(local, new ManifestTrustConfig(), "stable", false, TestContext.Current.CancellationToken)).Status);

        // A later session without network access.
        var server = new FakeHttpServer
        {
            Override = _ => throw new HttpRequestException("No such host is known.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)),
        };
        var offlineHttp = server.CreateClient();
        var offline = new ContentUpdateService(offlineHttp, paths, new GitHubReleasesClient(offlineHttp, paths.HttpCacheDirectory, Log.For<GitHubReleasesClient>()),
            Log.For<ContentUpdateService>());

        var result = await offline.CheckAsync(new ContentSourceConfig { Repository = "o/r" }, new ManifestTrustConfig(), "stable", false, TestContext.Current.CancellationToken);

        Assert.Equal(ContentCheckStatus.Offline, result.Status);
        Assert.Equal(ErrorCodes.SourceUnreachable, result.Error!.Code);
        Assert.Equal("1.0.0", offline.CachedManifest!.Manifest.ContentVersion);
    }

    [Fact]
    public void Developer_overrides_apply_only_in_developer_mode()
    {
        var distribution = new DistributionConfig { Content = new ContentSourceConfig { Repository = "team/content" } };
        var settings = new LauncherSettings { ContentSource = new ContentSourceOverride { Kind = ContentSourceKind.LocalFolder, Value = @"D:\test" } };
        Assert.Equal(ContentSourceType.GitHub, distribution.EffectiveContentSource(settings).Type);
        settings.DeveloperMode = true;
        var effective = distribution.EffectiveContentSource(settings);
        Assert.Equal(ContentSourceType.LocalFolder, effective.Type);
        Assert.Equal(@"D:\test", effective.LocalFolder);
    }
}
