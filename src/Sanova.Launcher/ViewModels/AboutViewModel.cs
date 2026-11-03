using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sanova.Core.Content;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;

namespace Sanova.Launcher.ViewModels;

public sealed record LinkItem(LocalizedString Label, string Url);

public sealed record CreditItem(string Name, string License, string? Url, string? Detail = null)
{
    public bool HasUrl => ShellIntegration.IsSafeWebLink(Url);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>About: the project, the disclaimer, third-party software and mod credits.</summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly AppState _state;

    public AboutViewModel(AppState state)
    {
        _state = state;
        VersionText = new LocalizedString(l => l.T("about.version", ("version", AppInfo.Version)));
        var links = state.Distribution.Links;
        Links = new (string Key, string? Url)[]
            {
                ("about.links.website", links.Website),
                ("about.links.source", links.SourceCode),
                ("about.links.community", links.Community),
                ("about.links.support", links.Support),
            }
            .Where(l => ShellIntegration.IsSafeWebLink(l.Url))
            .Select(l => new LinkItem(new LocalizedString(l.Key), l.Url!))
            .ToList();
        _state.PropertyChanged += OnStateChanged;
    }

    public LocalizedString VersionText { get; }

    public string InformationalVersion => AppInfo.InformationalVersion;

    public IReadOnlyList<LinkItem> Links { get; }

    public bool HasLinks => Links.Count > 0;

    public IReadOnlyList<CreditItem> ThirdParty { get; } =
    [
        new(".NET", "MIT", "https://github.com/dotnet/runtime"),
        new("Avalonia UI", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new("SkiaSharp and HarfBuzzSharp", "MIT", "https://github.com/mono/SkiaSharp"),
        new("ANGLE", "BSD-3-Clause", "https://chromium.googlesource.com/angle/angle"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Velopack", "MIT", "https://github.com/velopack/velopack"),
        new("Serilog", "Apache-2.0", "https://github.com/serilog/serilog"),
        new("Overpass typeface", "SIL Open Font License 1.1", "https://github.com/RedHatOfficial/Overpass"),
        new("Fluent UI System Icons", "MIT", "https://github.com/microsoft/fluentui-system-icons"),
    ];

    public IReadOnlyList<CreditItem> ModCredits
    {
        get
        {
            var manifest = _state.ComponentsManifest?.Manifest;
            if (manifest is null) return [];
            var loc = LocalizationService.Instance;
            return manifest.Components
                .Where(c => c.Authors.Count > 0)
                .Select(c => new CreditItem(
                    loc?.Resolve(c.Name) ?? c.Name.Default,
                    c.License ?? string.Empty,
                    c.Homepage,
                    loc?.T("mods.by", ("authors", string.Join(", ", c.Authors)))))
                .ToList();
        }
    }

    public bool HasModCredits => ModCredits.Count > 0;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.ComponentsManifest))
        {
            OnPropertyChanged(nameof(ModCredits));
            OnPropertyChanged(nameof(HasModCredits));
        }
    }

    [RelayCommand]
    private static void Open(string url) => ShellIntegration.OpenUrl(url);
}
