using System.Reflection;

namespace CampaignVault.Plugins;

/// <summary>
/// The <c>systems</c> a loaded plugin declared in plugin.json (set at host startup), keyed by its assembly. A plugin
/// with no entry, or an empty list, applies to every system. The host skips a plugin's handlers, observers, event
/// handlers, contributors and guidance in a campaign whose ActiveSystem is not listed.
/// </summary>
public static class PluginSystems
{
    private static IReadOnlyDictionary<Assembly, string[]> _byAssembly = new Dictionary<Assembly, string[]>();

    public static void Set(IEnumerable<(Assembly Assembly, IReadOnlyList<string>? Systems)> plugins) =>
        _byAssembly = plugins
            .Where(p => p.Systems is { Count: > 0 })
            .GroupBy(p => p.Assembly)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(p => p.Systems!).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray());

    /// <summary>
    /// True when the plugin owning <paramref name="component"/> applies to <paramref name="activeSystem"/>. A missing
    /// system (no campaign config) is treated as applicable so context-less callers keep working.
    /// </summary>
    public static bool AppliesTo(object component, string? activeSystem) => AppliesTo(component.GetType(), activeSystem);

    public static bool AppliesTo(Type componentType, string? activeSystem) =>
        string.IsNullOrWhiteSpace(activeSystem) ||
        !_byAssembly.TryGetValue(componentType.Assembly, out var systems) ||
        systems.Contains(activeSystem, StringComparer.OrdinalIgnoreCase);

    /// <summary>The listed systems for the plugin owning <paramref name="componentType"/>, for error messages.</summary>
    public static string Listed(Type componentType) =>
        _byAssembly.TryGetValue(componentType.Assembly, out var systems) ? string.Join(", ", systems) : "(all)";
}
