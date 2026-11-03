using System.Reflection;

namespace Sanova.Launcher.Infrastructure;

public static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    /// <summary>SemVer without build metadata, e.g. "0.1.0".</summary>
    public static string Version { get; } = (InformationalVersion.Split('+')[0]);

    /// <summary>Full informational version, including the commit hash when built from git.</summary>
    public static string InformationalVersion =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string UserAgent => Core.Downloads.SanovaHttp.UserAgent(Version);
}
