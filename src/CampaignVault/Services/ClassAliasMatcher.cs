using CampaignVault.Data.Templates;

namespace CampaignVault.Services;

/// <summary>
/// Single source of truth for "given a free-text class string the LLM wrote (e.g. 'Oath of Vengeance
/// Paladin', 'wizard (evocation)'), which <see cref="ClassDefinition"/> does it mean?".
///
/// Matching is longest-alias-wins so a subclass alias beats the base-class alias it contains
/// ("Eldritch Knight" over "Fighter"), and ties are broken by alias then class key so the result is
/// deterministic regardless of dictionary enumeration order — otherwise two identical campaigns could
/// resolve the same class string to different caster types.
///
/// Previously duplicated verbatim in <see cref="Dnd5eCasterLevelHelper"/> and
/// <see cref="Pf2eCasterClasses"/>, each with its own non-thread-safe lazily-built provider pointed at
/// the same temp extraction directory.
/// </summary>
public static class ClassAliasMatcher
{
    // Both call sites previously kept their own `??=` lazy field over the same extraction path, so two
    // threads could race to extract into it concurrently. Lazy<T> is thread-safe by default and gives
    // the whole process one shared provider instead of one per helper class.
    private static readonly Lazy<ClassDefinitionProvider> SharedProvider = new(() =>
        new ClassDefinitionProvider(
            Path.Combine(Path.GetTempPath(), "cv_classdef_embedded"),
            typeof(ClassDefinitionProvider).Assembly));

    /// <summary>
    /// Fallback provider for call sites that don't inject one (tests, legacy code).
    /// </summary>
    public static ClassDefinitionProvider DefaultProvider => SharedProvider.Value;

    /// <summary>
    /// Resolves a free-text class string to the best-matching definition for <paramref name="system"/>,
    /// or null when no alias appears in it.
    /// </summary>
    public static ClassDefinition? Resolve(
        string className,
        string system,
        ClassDefinitionProvider? provider = null) =>
        Resolve(className, (provider ?? DefaultProvider).GetClassesForSystem(system));

    /// <summary>
    /// Resolves against an already-loaded class table — use this in loops so the definitions are
    /// fetched once rather than once per class level entry.
    /// </summary>
    public static ClassDefinition? Resolve(
        string className,
        IReadOnlyDictionary<string, ClassDefinition> classDefs)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        ClassDefinition? best = null;
        string? bestAlias = null;
        string? bestKey = null;

        foreach (var (key, def) in classDefs)
        {
            foreach (var alias in def.Aliases)
            {
                if (alias.Length == 0 || !className.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (bestAlias == null
                    || alias.Length > bestAlias.Length
                    || (alias.Length == bestAlias.Length && IsDeterministicallyBefore(alias, key, bestAlias, bestKey!)))
                {
                    best = def;
                    bestAlias = alias;
                    bestKey = key;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Resolves the caster type for a free-text class string; <see cref="CasterType.None"/> when the
    /// string matches no known class (an unknown class contributes no spell slots rather than
    /// silently inheriting whichever definition happened to enumerate first).
    /// </summary>
    public static CasterType ResolveCasterType(
        string className,
        IReadOnlyDictionary<string, ClassDefinition> classDefs) =>
        Resolve(className, classDefs)?.CasterType ?? CasterType.None;

    private static bool IsDeterministicallyBefore(string alias, string key, string bestAlias, string bestKey)
    {
        var aliasOrder = string.CompareOrdinal(alias, bestAlias);
        return aliasOrder != 0 ? aliasOrder < 0 : string.CompareOrdinal(key, bestKey) < 0;
    }
}
