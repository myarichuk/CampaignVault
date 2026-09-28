using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Movement in feet per round, what the DM needs to adjudicate a chase or a flight. Status effects and armour move it
/// (<c>Speed</c> modifiers, <c>MovementModifier</c>); plugin providers add to it through <see cref="RollModifierPipeline.Speed"/>.
/// This static view is the cheap, context-free one used for cards and views.
/// </summary>
public static class SpeedRules
{
    /// <summary>Never slower than this: even a hobbled captive can be walked or dragged.</summary>
    public const int Floor = 5;

    /// <summary>Travel time is never stretched beyond this multiple, however slow someone is.</summary>
    public const double MaxTravelStretch = 3.0;

    public static int? Base(SystemExtension? stats) => stats?.Movement is { } m ? (int)Math.Floor(m) : null;

    /// <summary>Base plus armour plus every status <c>Speed</c> modifier, floored; null when the character has no numeric movement.</summary>
    public static int? Effective(SystemExtension? stats, bool includeArmor = true)
    {
        if (Base(stats) is not { } baseFeet)
            return null;
        var armor = includeArmor ? stats!.MovementModifier : 0f;
        var status = StatusEffectModifierProvider.Sum(stats!.StatusEffects, RollKinds.Speed, null);
        return Math.Max(Floor, (int)Math.Floor(baseFeet + armor) + status);
    }

    /// <summary>
    /// "10 ft (30 normally: Hobbled −20)" when something moves it, else "30 ft". Null when unknown. The reasons are the names of
    /// the status effects carrying a Speed modifier, plus "armour".
    /// </summary>
    public static string? Describe(SystemExtension? stats)
    {
        if (Effective(stats) is not { } now || Base(stats) is not { } normal)
            return null;
        if (now == normal)
            return $"{now} ft";

        var why = stats!.StatusEffects
            .Where(e => e.StatModifiers.Any(kv => StatusEffectModifierProvider.Normalize(kv.Key) == "speed" && kv.Value != 0))
            .Select(e => e.Name)
            .ToList();
        if (stats.MovementModifier != 0)
            why.Add("armour");
        return $"{now} ft ({normal} normally{(why.Count == 0 ? "" : ": " + string.Join(", ", why))})";
    }

    /// <summary>
    /// How much longer a trip takes because of this character's <em>temporary</em> slowness (status effects and providers, not
    /// armour): base speed over current speed, between 1 (never faster) and <see cref="MaxTravelStretch"/>.
    /// </summary>
    public static double TravelStretch(Character character, RollModifierPipeline pipeline, IReadOnlyDictionary<string, string> options, string? system)
    {
        if (Base(character.SystemStats) is not { } normal || normal <= 0)
            return 1.0;
        var now = pipeline.Speed(character, options, system, includeArmor: false);
        if (now <= 0 || now >= normal)
            return 1.0;
        return Math.Min(MaxTravelStretch, (double)normal / now);
    }
}
