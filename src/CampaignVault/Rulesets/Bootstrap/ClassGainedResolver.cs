using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>The class a level goes to: its name as the sheet spells it, the level it has now (0 when new), or why it can't be resolved.</summary>
internal sealed record ClassGain(string? ClassName, int CurrentLevel, bool IsNew, string? Error)
{
    public static ClassGain Failure(string error) => new(null, 0, false, error);
}

/// <summary>
/// The one answer to "which class gains this level", shared by the planner (which choices the level asks), the hit point step
/// (which hit die) and the class-level sync (which entry grows). A class name that matches nothing on the sheet is a new
/// class (5e multiclassing) rather than a fallback to the first or last one. Names compare as whole class names, so "War"
/// is not "Warlock".
/// </summary>
internal static class ClassGainedResolver
{
    public static ClassGain Resolve(Character character, string system, string? classGained)
    {
        var entries = CharacterClassResolver.ResolveClassLevels(character);
        var is5e = string.Equals(system, RulesetSystem.Dnd5e, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(classGained))
        {
            return entries.Count switch
            {
                0 => new ClassGain(null, 0, false, null), // no class recorded: the stats alone carry the level
                1 => new ClassGain(entries[0].Class, entries[0].Level, false, null),
                _ => ClassGain.Failure(
                    $"{character.Name} has more than one class ({string.Join(", ", entries.Select(e => $"{e.Class} {e.Level}"))}): "
                    + "say which class gains the level with classGained."),
            };
        }

        var wanted = classGained.Trim();
        var match = entries.FirstOrDefault(e => SameClass(e.Class, wanted));
        if (match is not null)
        {
            return new ClassGain(match.Class, match.Level, false, null);
        }

        if (!is5e)
        {
            return ClassGain.Failure(
                $"{character.Name} has no class '{wanted}'."
                + (string.Equals(system, RulesetSystem.Pathfinder2e, StringComparison.OrdinalIgnoreCase)
                    ? " Pathfinder doesn't multiclass by level: it takes a multiclass archetype feat instead."
                    : ""));
        }

        var known = Dnd5eClassProfileResolver.CanonicalClass(wanted);
        return known is null
            ? ClassGain.Failure($"'{wanted}' is not a class this ruleset knows ({string.Join(", ", Dnd5eClassProfileResolver.ClassNames)}).")
            : new ClassGain(Display(known), 0, true, null);
    }

    /// <summary>
    /// Gives a 5e character kept as text ("Fighter 5", or "Human Wizard" with the level on the stats) structured class entries
    /// at the levels it has now, so a second class can be added to them. Done before the level is counted.
    /// </summary>
    public static void EnsureStructuredEntries(Character character)
    {
        if (character.SystemStats is Dnd5eExtension { ClassLevels.Count: 0 } stats
            && CharacterClassResolver.ResolveClassLevels(character) is { Count: > 0 } current)
        {
            stats.ClassLevels = [.. current.Select(e => new ClassLevelEntry { Class = e.Class, Level = e.Level })];
        }
    }

    /// <summary>Whether two class names are the same class: equal, or the same SRD class once decoration ("Fighter (Champion)") is stripped.</summary>
    public static bool SameClass(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var left = Dnd5eClassProfileResolver.CanonicalClass(a);
        return left is not null && string.Equals(left, Dnd5eClassProfileResolver.CanonicalClass(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The 5e multiclass requirements (SRD 5.1) a character misses for <paramref name="gain"/>: the new class's, and every
    /// current class's, each as a sentence naming the ability and the score. Empty when it may take the class. Only a new
    /// class is checked; adding a level to a class already on the sheet needs nothing.
    /// </summary>
    public static IReadOnlyList<string> UnmetPrerequisites(
        Character character, ClassGain gain, ClassDefinitionProvider? classes, string system)
    {
        if (!gain.IsNew || gain.ClassName is null || classes is null || character.SystemStats is not Dnd5eExtension stats)
        {
            return [];
        }

        const int minimum = 13;
        var unmet = new List<string>();
        var involved = CharacterClassResolver.ResolveClassLevels(character).Select(e => e.Class).Prepend(gain.ClassName);
        foreach (var name in involved.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!classes.TryResolveClass(system, name, out var definition))
            {
                continue;
            }

            foreach (var requirement in definition.MulticlassRequires.Where(r => r.Count > 0))
            {
                var best = requirement.Select(a => (Ability: a, Score: AbilityScore(stats, a))).MaxBy(x => x.Score);
                if (best.Score >= minimum)
                {
                    continue;
                }

                var abilities = string.Join(" or ", requirement);
                var scores = string.Join(", ", requirement.Select(a => $"{a} {AbilityScore(stats, a)}"));
                unmet.Add($"{(name == gain.ClassName ? "taking" : "leaving")} {Display(name)} needs {abilities} {minimum} ({scores}).");
            }
        }

        return unmet;
    }

    private static string Display(string className) =>
        Dnd5eClassProfileResolver.CanonicalClass(className) is { } c ? char.ToUpperInvariant(c[0]) + c[1..] : className;

    private static int AbilityScore(Dnd5eExtension stats, string ability) => ability.Trim().ToLowerInvariant() switch
    {
        "strength" or "str" => stats.Strength,
        "dexterity" or "dex" => stats.Dexterity,
        "constitution" or "con" => stats.Constitution,
        "intelligence" or "int" => stats.Intelligence,
        "wisdom" or "wis" => stats.Wisdom,
        "charisma" or "cha" => stats.Charisma,
        _ => 0,
    };
}
