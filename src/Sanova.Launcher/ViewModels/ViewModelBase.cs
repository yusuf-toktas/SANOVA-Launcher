using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sanova.Core.Game;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Called each time the page becomes visible.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}

/// <summary>Looks up vector icons defined in Themes/Icons.axaml.</summary>
public static class Icons
{
    public static Geometry? Get(string name)
        => Application.Current?.TryGetResource("Icon." + name, null, out var value) == true ? value as Geometry : null;
}

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string iconName, ViewModelBase page)
    {
        Key = key;
        Label = new LocalizedString($"nav.{key}");
        IconName = iconName;
        Page = page;
    }

    public string Key { get; }

    public LocalizedString Label { get; }

    public string IconName { get; }

    public Geometry? Icon => Icons.Get(IconName);

    public ViewModelBase Page { get; }

    [ObservableProperty]
    private bool _hasBadge;
}

/// <summary>Presentation helpers shared by several pages.</summary>
public static class Present
{
    public static string Executable(LocalizationService l, ExecutableIdentity identity) => identity.Kind switch
    {
        ExecutableKind.Known when identity.Match is { Version: not null } match
            => l.T("exe.named", ("version", match.Version), ("region", match.Region ?? "?")),
        ExecutableKind.Known => l.T("exe.kind.modifiedUnknown"),
        ExecutableKind.ModernRerelease => l.T("exe.kind.rerelease"),
        ExecutableKind.RetailUnrecognized => l.T("exe.kind.retailUnrecognized"),
        ExecutableKind.DefinitiveEdition => l.T("exe.kind.definitive"),
        ExecutableKind.Missing => l.T("exe.kind.missing"),
        _ => l.T("exe.kind.unknown"),
    };

    public static string Edition(LocalizationService l, ExecutableIdentity identity) => identity.Match?.Edition switch
    {
        ExecutableEdition.Retail => l.T("exe.edition.retail"),
        ExecutableEdition.Modified => l.T("exe.edition.modified"),
        ExecutableEdition.Steam => l.T("exe.edition.steam"),
        ExecutableEdition.RockstarLauncher => l.T("exe.edition.rockstar"),
        _ => string.Empty,
    };

    public static string Source(LocalizationService l, GameDetectionSource source) => l.T(source switch
    {
        GameDetectionSource.Registry => "detect.registry",
        GameDetectionSource.Uninstall => "detect.uninstall",
        GameDetectionSource.Steam => "detect.steam",
        GameDetectionSource.SampClient => "detect.samp",
        GameDetectionSource.RockstarLauncher => "detect.rockstar",
        GameDetectionSource.Saved => "detect.saved",
        GameDetectionSource.Manual => "detect.manual",
        _ => "detect.common",
    });

    public static Tone Tone(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Error => Services.Tone.Danger,
        FindingSeverity.Warning => Services.Tone.Warning,
        _ => Services.Tone.Success,
    };
}

/// <summary>A compatibility finding rendered for the UI.</summary>
public sealed class FindingItem
{
    public FindingItem(CompatibilityFinding finding)
    {
        Finding = finding;
        Title = new LocalizedString(l => l.T($"compat.{finding.Code}.title", finding.Args));
        Body = new LocalizedString(l => l.T($"compat.{finding.Code}.body", finding.Args));
        Items = finding.Items.Take(12).ToList();
        MoreItems = new LocalizedString(l => finding.Items.Count > 12 ? l.Plural("compat.moreItems", finding.Items.Count - 12) : string.Empty);
    }

    public CompatibilityFinding Finding { get; }

    public LocalizedString Title { get; }

    public LocalizedString Body { get; }

    public IReadOnlyList<string> Items { get; }

    public LocalizedString MoreItems { get; }

    public bool HasItems => Items.Count > 0;

    public bool IsError => Finding.Severity == FindingSeverity.Error;

    public bool IsWarning => Finding.Severity == FindingSeverity.Warning;

    public bool IsInfo => Finding.Severity == FindingSeverity.Info;

    public Geometry? Icon => Icons.Get(IsError ? "Error" : IsWarning ? "Warning" : "CheckCircle");
}
