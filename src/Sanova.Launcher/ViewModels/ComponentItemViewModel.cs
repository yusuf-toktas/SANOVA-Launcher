using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sanova.Core.Content;
using Sanova.Launcher.Infrastructure;

namespace Sanova.Launcher.ViewModels;

/// <summary>One SANOVA component (mod) as shown in the Mods page and the setup wizard.</summary>
public sealed partial class ComponentItemViewModel : ObservableObject
{
    private bool _suppress;

    [ObservableProperty]
    private bool _isEnabled;

    public ComponentItemViewModel(ComponentSpec spec, ContentManifest manifest, bool enabled)
    {
        Spec = spec;
        _isEnabled = enabled;
        var size = manifest.SizeOf(spec.Id);
        Name = new LocalizedString(l => l.Resolve(spec.Name));
        Description = new LocalizedString(l => l.Resolve(spec.Description));
        Size = new LocalizedString(l => l.Bytes(size));
        Authors = new LocalizedString(l => spec.Authors.Count > 0 ? l.T("mods.by", ("authors", string.Join(", ", spec.Authors))) : string.Empty);
        License = new LocalizedString(l => spec.License is { Length: > 0 } license ? l.T("mods.license", ("license", license)) : l.T("mods.licenseUnknown"));
        Redistribution = new LocalizedString(l => l.T($"mods.redistribution.{spec.Redistribution.ToString().ToLowerInvariant()}"));
        SourceNote = new LocalizedString(l => spec.SourceKind switch
        {
            ComponentSourceKind.External => l.T("mods.source.external"),
            ComponentSourceKind.Manual => l.T("mods.source.manual"),
            _ => string.Empty,
        });
        Dependencies = new LocalizedString(l =>
        {
            var names = spec.Dependencies.Select(manifest.FindComponent).OfType<ComponentSpec>().Select(d => l.Resolve(d.Name)).ToList();
            return names.Count > 0 ? l.T("mods.requires", ("names", string.Join(", ", names))) : string.Empty;
        });
    }

    public ComponentSpec Spec { get; }

    public string Id => Spec.Id;

    public LocalizedString Name { get; }

    public LocalizedString Description { get; }

    public LocalizedString Size { get; }

    public LocalizedString Authors { get; }

    public LocalizedString License { get; }

    public LocalizedString Redistribution { get; }

    public LocalizedString SourceNote { get; }

    public LocalizedString Dependencies { get; }

    public bool IsRequired => Spec.Required;

    public bool IsOptional => !Spec.Required;

    public bool HasHomepage => ShellIntegration.IsSafeWebLink(Spec.Homepage);

    public bool HasSourceNote => Spec.SourceKind != ComponentSourceKind.Package;

    public bool HasDependencies => Spec.Dependencies.Count > 0;

    public bool HasDescription => Spec.Description is { Count: > 0 };

    public bool HasAuthors => Spec.Authors.Count > 0;

    /// <summary>Raised when the player flips the switch (not when the owner syncs state).</summary>
    public Action<ComponentItemViewModel, bool>? Toggled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppress) Toggled?.Invoke(this, value);
    }

    /// <summary>Updates the switch without raising <see cref="Toggled"/>.</summary>
    public void Sync(bool enabled)
    {
        _suppress = true;
        IsEnabled = enabled;
        _suppress = false;
    }

    [RelayCommand]
    private void OpenHomepage() => ShellIntegration.OpenUrl(Spec.Homepage);
}

public sealed class ComponentGroup
{
    public ComponentGroup(string id, LocalizedString title, IReadOnlyList<ComponentItemViewModel> items)
    {
        Id = id;
        Title = title;
        Items = items;
    }

    public string Id { get; }

    public LocalizedString Title { get; }

    public IReadOnlyList<ComponentItemViewModel> Items { get; }
}
