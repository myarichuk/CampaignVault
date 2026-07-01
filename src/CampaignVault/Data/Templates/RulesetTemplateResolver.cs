using Microsoft.Extensions.Logging;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Resolves a template by walking its Inherits chain and merging parent values into the child.
/// Decoupled from storage via a lookup delegate. The merge delegate is the only type-specific piece.
/// </summary>
public class RulesetTemplateResolver<T> where T : RulesetTemplate
{
    private readonly Func<string, T?> _lookup;
    private readonly Func<T, T, T> _merge;

    /// <param name="lookup">Returns a raw (unresolved) template by name, or null if not found.</param>
    /// <param name="merge">Merges two templates: (child, parent) → result. Child fields win.</param>
    public RulesetTemplateResolver(Func<string, T?> lookup, Func<T, T, T> merge)
    {
        _lookup = lookup;
        _merge = merge;
    }

    /// <summary>
    /// Resolves every template in <paramref name="raw"/>, skipping (and logging) any entry whose
    /// inheritance chain is broken (missing parent or a cycle) instead of throwing, so one
    /// malformed homebrew YAML file doesn't take down every other template for that system.
    /// </summary>
    public IReadOnlyDictionary<string, T> ResolveAll(
        IReadOnlyDictionary<string, T> raw,
        ILogger? logger = null)
    {
        var resolved = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, template) in raw)
        {
            try
            {
                resolved[name] = Resolve(template);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                logger?.LogWarning(ex,
                    "Skipping template '{Name}': failed to resolve inheritance (missing parent or cycle).",
                    name);
            }
        }

        return resolved;
    }

    public T Resolve(T child) => Resolve(child, []);

    private T Resolve(T child, HashSet<string> stack)
    {
        if (child.Inherits.Count == 0)
            return child;

        if (!stack.Add(child.Name))
            throw new InvalidOperationException(
                $"Circular inheritance detected involving template '{child.Name}'.");

        var result = child;
        foreach (var parentName in child.Inherits)
        {
            var rawParent = _lookup(parentName)
                ?? throw new KeyNotFoundException(
                    $"Template '{parentName}' not found (referenced by '{child.Name}').");

            var resolvedParent = Resolve(rawParent, stack);
            result = _merge(result, resolvedParent);
        }

        stack.Remove(child.Name);
        return result;
    }
}
