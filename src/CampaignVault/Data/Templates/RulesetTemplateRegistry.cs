using System.Diagnostics.CodeAnalysis;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Singleton registry. Holds one loader + merge-function pair per concrete template type.
/// Resolves inheritance on first access and caches results.
/// Call Reload() to clear the cache during development hot-reload.
/// </summary>
public class RulesetTemplateRegistry
{
    private record Registration(object Loader, object MergeFunc);

    private readonly Dictionary<Type, Registration> _registrations = new();
    private readonly Dictionary<Type, object> _rawCache = new();
    private readonly Dictionary<(Type, string), object> _resolvedCache = new();
    private readonly object _lock = new();

    public void Register<T>(RulesetTemplateLoader<T> loader, Func<T, T, T> merge)
        where T : RulesetTemplate
    {
        lock (_lock)
        {
            _registrations[typeof(T)] = new Registration(loader, merge);
            _rawCache.Remove(typeof(T));
            _resolvedCache.Keys
                .Where(k => k.Item1 == typeof(T))
                .ToList()
                .ForEach(k => _resolvedCache.Remove(k));
        }
    }

    public bool TryGet<T>(string name, [NotNullWhen(true)] out T? template)
        where T : RulesetTemplate
    {
        lock (_lock)
        {
            var key = (typeof(T), name);
            if (_resolvedCache.TryGetValue(key, out var cached))
            {
                template = (T)cached;
                return true;
            }

            var rawTemplates = GetOrLoadRaw<T>();
            if (rawTemplates == null || !rawTemplates.TryGetValue(name, out var raw))
            {
                template = null;
                return false;
            }

            var entry = _registrations[typeof(T)];
            var merge = (Func<T, T, T>)entry.MergeFunc;
            var resolver = new RulesetTemplateResolver<T>(
                n => rawTemplates.TryGetValue(n, out var t) ? t : null,
                merge);

            var resolved = resolver.Resolve(raw);
            _resolvedCache[key] = resolved;
            template = resolved;
            return true;
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            _rawCache.Clear();
            _resolvedCache.Clear();
        }
    }

    private IReadOnlyDictionary<string, T>? GetOrLoadRaw<T>() where T : RulesetTemplate
    {
        if (_rawCache.TryGetValue(typeof(T), out var cached))
            return (IReadOnlyDictionary<string, T>)cached;

        if (!_registrations.TryGetValue(typeof(T), out var entry))
            return null;

        var loader = (RulesetTemplateLoader<T>)entry.Loader;
        var templates = loader.Load();
        _rawCache[typeof(T)] = templates;
        return templates;
    }
}
