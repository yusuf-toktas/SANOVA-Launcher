using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Sanova.Launcher.Infrastructure;

/// <summary>Opens folders and web links. Links from manifests are untrusted, so only http(s) is allowed.</summary>
public static class ShellIntegration
{
    public static bool OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Could not open folder {Path}", path);
            return false;
        }
    }

    public static bool RevealFile(string path)
    {
        if (!File.Exists(path)) return OpenFolder(Path.GetDirectoryName(path));
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Could not reveal {Path}", path);
            return false;
        }
    }

    public static bool IsSafeWebLink(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public static bool OpenUrl(string? url)
    {
        if (!IsSafeWebLink(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Could not open link");
            return false;
        }
    }

    /// <summary>Honors the Windows "Animation effects" accessibility setting.</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            const uint spiGetClientAreaAnimation = 0x1042;
            var enabled = true;
            return !SystemParametersInfo(spiGetClientAreaAnimation, 0, ref enabled, 0) || enabled;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);
}
