using System.Text.RegularExpressions;

namespace Sanova.Core.Diagnostics;

/// <summary>
/// Removes personal details (profile path, account name, computer name) from text that a
/// player may share publicly when reporting a problem.
/// </summary>
public sealed class Redactor
{
    private readonly string? _profile;
    private readonly Regex? _userName;
    private readonly Regex? _machineName;

    public Redactor(string? userProfile = null, string? userName = null, string? machineName = null)
    {
        _profile = NullIfBlank(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _userName = WordPattern(userName ?? Environment.UserName);
        _machineName = WordPattern(machineName ?? Environment.MachineName);
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;
        if (_profile is not null)
        {
            result = result.Replace(_profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(_profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(_profile.Replace("\\", "\\\\", StringComparison.Ordinal), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        if (_userName is not null) result = _userName.Replace(result, "<user>");
        if (_machineName is not null) result = _machineName.Replace(result, "<computer>");
        return result;
    }

    private static Regex? WordPattern(string? value)
    {
        // Very short names would redact ordinary words; profile-path redaction still covers them.
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return null;
        return new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('\\', '/');
}
