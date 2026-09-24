using System.Reflection;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Events;

namespace CampaignVault.Data.Events;

/// <summary>
/// Maps a publishing type's assembly to its domain-event source prefix: <see cref="CoreEvents.Source"/> for the
/// host and SDK, the manifest id for a loaded plugin, the lowercased assembly name for anything else. Also
/// holds the topics plugins declared they publish, for the startup subscription check.
/// </summary>
public sealed class PluginEventSources
{
    private static readonly Assembly HostAssembly = typeof(PluginEventSources).Assembly;
    private static readonly Assembly SdkAssembly = typeof(IChangeContext).Assembly;

    private readonly Dictionary<Assembly, string> _idsByAssembly = [];
    private readonly HashSet<string> _declaredTopics = new(CoreEvents.All, StringComparer.OrdinalIgnoreCase);

    public static PluginEventSources CoreOnly { get; } = new([]);

    public PluginEventSources(IEnumerable<(Assembly Assembly, string? Id, IReadOnlyList<string> Publishes)> plugins)
    {
        foreach (var (assembly, id, publishes) in plugins)
        {
            // A plugin may never claim the core prefix, or it could spoof core events.
            var source = string.IsNullOrWhiteSpace(id) || string.Equals(id, CoreEvents.Source, StringComparison.OrdinalIgnoreCase)
                ? FallbackSource(assembly)
                : id.Trim().ToLowerInvariant();
            _idsByAssembly[assembly] = source;

            foreach (var topic in publishes.Where(t => CoreEvents.IsOwnedBy(t, source)))
            {
                _declaredTopics.Add(topic);
            }
        }
    }

    public IReadOnlySet<string> DeclaredTopics => _declaredTopics;

    public IReadOnlyCollection<string> PluginSources => _idsByAssembly.Values;

    public string SourceFor(Type publisher)
    {
        var assembly = publisher.Assembly;
        if (assembly == HostAssembly || assembly == SdkAssembly)
        {
            return CoreEvents.Source;
        }

        return _idsByAssembly.TryGetValue(assembly, out var id) ? id : FallbackSource(assembly);
    }

    private static string FallbackSource(Assembly assembly) =>
        (assembly.GetName().Name ?? "unknown").ToLowerInvariant();
}
