namespace Sanova.Core.Content;

public enum SelectionChangeReason
{
    Requested,
    RequiredByDependency,
    DependentDisabled,
    ConflictDisabled,
}

/// <summary>A component switched on or off as a consequence of the player's choice.</summary>
public sealed record SelectionChange(string ComponentId, bool Enabled, SelectionChangeReason Reason, string? CausedBy);

/// <summary>
/// Keeps optional-component selections consistent: required components are always on,
/// enabling a component enables its dependencies, disabling one disables everything that
/// depends on it, and enabling one disables what it conflicts with.
/// </summary>
public static class ComponentResolver
{
    public static HashSet<string> InitialSelection(ContentManifest manifest, IReadOnlyDictionary<string, bool>? choices)
    {
        var selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in manifest.Components.OrderBy(c => c.Required ? 0 : 1))
        {
            var enabled = component.Required ||
                          (choices is not null && choices.TryGetValue(component.Id, out var chosen) ? chosen : component.DefaultEnabled);
            if (enabled)
            {
                Toggle(manifest, selection, component.Id, enable: true, out _);
            }
        }
        foreach (var required in manifest.Components.Where(c => c.Required))
        {
            selection.Add(required.Id);
        }
        return selection;
    }

    /// <summary>Applies one toggle in place and reports the cascade of resulting changes.</summary>
    public static bool Toggle(ContentManifest manifest, HashSet<string> selection, string componentId, bool enable, out List<SelectionChange> changes)
    {
        changes = [];
        var component = manifest.FindComponent(componentId);
        if (component is null) return false;
        if (!enable && component.Required) return false;

        if (enable)
        {
            Enable(manifest, selection, component, SelectionChangeReason.Requested, null, changes, []);
        }
        else
        {
            Disable(manifest, selection, component, SelectionChangeReason.Requested, null, changes);
        }
        return true;
    }

    private static void Enable(ContentManifest manifest, HashSet<string> selection, ComponentSpec component, SelectionChangeReason reason,
        string? causedBy, List<SelectionChange> changes, HashSet<string> visiting)
    {
        if (!visiting.Add(component.Id)) return;

        // A component that conflicts with an active required component can never be enabled.
        if (ConflictsOf(manifest, component).Any(other => other.Required && selection.Contains(other.Id))) return;

        foreach (var dependencyId in component.Dependencies)
        {
            var dependency = manifest.FindComponent(dependencyId);
            if (dependency is not null && !selection.Contains(dependency.Id))
            {
                Enable(manifest, selection, dependency, SelectionChangeReason.RequiredByDependency, component.Id, changes, visiting);
            }
        }

        foreach (var other in ConflictsOf(manifest, component))
        {
            if (selection.Contains(other.Id) && !other.Required)
            {
                Disable(manifest, selection, other, SelectionChangeReason.ConflictDisabled, component.Id, changes);
            }
        }

        if (selection.Add(component.Id))
        {
            changes.Add(new SelectionChange(component.Id, true, reason, causedBy));
        }
    }

    private static void Disable(ContentManifest manifest, HashSet<string> selection, ComponentSpec component, SelectionChangeReason reason,
        string? causedBy, List<SelectionChange> changes)
    {
        if (component.Required || !selection.Remove(component.Id)) return;
        changes.Add(new SelectionChange(component.Id, false, reason, causedBy));

        foreach (var dependent in manifest.Components.Where(c =>
                     selection.Contains(c.Id) && c.Dependencies.Contains(component.Id, StringComparer.OrdinalIgnoreCase)))
        {
            Disable(manifest, selection, dependent, SelectionChangeReason.DependentDisabled, component.Id, changes);
        }
    }

    private static IEnumerable<ComponentSpec> ConflictsOf(ContentManifest manifest, ComponentSpec component)
        => manifest.Components.Where(other =>
            !string.Equals(other.Id, component.Id, StringComparison.OrdinalIgnoreCase) &&
            (component.Conflicts.Contains(other.Id, StringComparer.OrdinalIgnoreCase) ||
             other.Conflicts.Contains(component.Id, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Lists problems with a selection (used before applying it).</summary>
    public static IReadOnlyList<string> Problems(ContentManifest manifest, IReadOnlySet<string> selection)
    {
        var problems = new List<string>();
        foreach (var component in manifest.Components)
        {
            if (component.Required && !selection.Contains(component.Id))
            {
                problems.Add($"Required component '{component.Id}' is not selected.");
            }
            if (!selection.Contains(component.Id)) continue;
            foreach (var dependency in component.Dependencies.Where(d => !selection.Contains(d)))
            {
                problems.Add($"'{component.Id}' requires '{dependency}'.");
            }
            foreach (var conflict in ConflictsOf(manifest, component).Where(c => selection.Contains(c.Id)))
            {
                if (string.CompareOrdinal(component.Id, conflict.Id) < 0)
                {
                    problems.Add($"'{component.Id}' conflicts with '{conflict.Id}'.");
                }
            }
        }
        foreach (var id in selection.Where(id => manifest.FindComponent(id) is null))
        {
            problems.Add($"Unknown component '{id}'.");
        }
        return problems;
    }
}
