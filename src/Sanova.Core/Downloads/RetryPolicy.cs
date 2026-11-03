using System.Net.Sockets;

namespace Sanova.Core.Downloads;

/// <summary>Exponential backoff with jitter for transient network failures.</summary>
public sealed class RetryPolicy
{
    public static RetryPolicy Default { get; } = new();

    public int MaxAttempts { get; init; } = 4;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan GetDelay(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero)
        {
            return serverDelay < TimeSpan.FromMinutes(1) ? serverDelay : TimeSpan.FromMinutes(1);
        }
        var exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        var jitter = Random.Shared.NextDouble() * capped * 0.25;
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    /// <summary>True for failures that are worth retrying against the same server.</summary>
    public static bool IsTransient(Exception ex, CancellationToken userToken) => ex switch
    {
        OperationCanceledException => !userToken.IsCancellationRequested,
        HttpStatusException status => status.IsTransient,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || (int)code is 408 or 429,
        HttpRequestException => true,
        TimeoutException => true,
        RestartDownloadException => true,
        IOException io => io.InnerException is SocketException || io is not FileNotFoundException and not DirectoryNotFoundException,
        _ => false,
    };

    /// <summary>True when the failure means there is no usable network at all.</summary>
    public static bool IsOffline(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NetworkUnreachable
                    or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.TryAgain or SocketError.NoData)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>The partially downloaded data was unusable; retry from scratch.</summary>
public sealed class RestartDownloadException(string message) : Exception(message);
