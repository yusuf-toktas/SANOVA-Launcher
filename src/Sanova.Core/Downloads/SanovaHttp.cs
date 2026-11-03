using System.Net;
using System.Net.Http.Headers;

namespace Sanova.Core.Downloads;

/// <summary>Factory for the launcher's two HTTP clients.</summary>
public static class SanovaHttp
{
    public static string UserAgent(string launcherVersion) => $"SANOVA-Launcher/{launcherVersion}";

    /// <summary>For JSON APIs and manifests: compressed transfer, short timeouts.</summary>
    public static HttpClient CreateApiClient(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>
    /// For package downloads: identity encoding (byte ranges must address the stored bytes)
    /// and no overall timeout — stalled transfers are detected per read instead.
    /// </summary>
    public static HttpClient CreateDownloadClient(string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return client;
    }
}

/// <summary>A non-success HTTP status, carrying what retry decisions need.</summary>
public sealed class HttpStatusException : Exception
{
    public HttpStatusException(HttpStatusCode statusCode, Uri uri, TimeSpan? retryAfter = null)
        : base($"HTTP {(int)statusCode} for {uri}")
    {
        StatusCode = statusCode;
        Uri = uri;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode StatusCode { get; }

    public Uri Uri { get; }

    public TimeSpan? RetryAfter { get; }

    public bool IsTransient => (int)StatusCode >= 500 || StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    public static HttpStatusException From(HttpResponseMessage response, Uri uri)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => null,
        };
        return new HttpStatusException(response.StatusCode, uri, retryAfter);
    }
}

/// <summary>Raised when a server ignores HTTP range requests; callers fall back to full downloads.</summary>
public sealed class RangeNotSupportedException(Uri uri) : Exception($"Server does not support byte ranges: {uri}");
