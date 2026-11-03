using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Sanova.Core.Game;

public enum RegHive
{
    CurrentUser,
    LocalMachine,
}

public enum RegView
{
    Default,
    Registry32,
    Registry64,
}

/// <summary>Read-only registry access, abstracted so detection logic can be unit tested.</summary>
public interface IRegistryReader
{
    string? GetString(RegHive hive, RegView view, string subKey, string valueName);

    IReadOnlyList<string> GetSubKeyNames(RegHive hive, RegView view, string subKey);
}

public sealed class NullRegistryReader : IRegistryReader
{
    public string? GetString(RegHive hive, RegView view, string subKey, string valueName) => null;

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, RegView view, string subKey) => [];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryReader : IRegistryReader
{
    public string? GetString(RegHive hive, RegView view, string subKey, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(Map(hive), Map(view));
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, RegView view, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(Map(hive), Map(view));
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static RegistryHive Map(RegHive hive) => hive == RegHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static RegistryView Map(RegView view) => view switch
    {
        RegView.Registry32 => RegistryView.Registry32,
        RegView.Registry64 => RegistryView.Registry64,
        _ => RegistryView.Default,
    };
}
