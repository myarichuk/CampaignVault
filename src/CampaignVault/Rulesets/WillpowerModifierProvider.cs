using CampaignVault.Data.ChangeHandlers;

namespace CampaignVault.Rulesets;

/// <summary>
/// What willpower does: it steadies or breaks a character against fear, charm, compulsion and other mental pressure. Only a save
/// tagged <c>charm</c>, <c>fear</c>, <c>compulsion</c> or <c>mental</c> is touched. A default character (75) is unaffected, so
/// nothing changes until something lowers it or the DM raises it.
/// </summary>
internal sealed class WillpowerModifierProvider : IRollModifierProvider
{
    private static readonly string[] MentalTags = ["charm", "fear", "compulsion", "mental"];

    public IEnumerable<RollModifier> Modifiers(RollQuery query)
    {
        if (query.Kind != RollKinds.Save || query.Actor.SystemStats is null)
            yield break;
        var tag = MentalTags.FirstOrDefault(t => query.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
        if (tag is null)
            yield break;

        var will = query.Actor.SystemStats.Willpower;
        var (bonus, advantage) = Band(will);
        if (bonus == 0 && advantage == AdvantageEffect.None)
            yield break;

        var effect = advantage == AdvantageEffect.Disadvantage ? $"{bonus:+0;-0}, disadvantage" : $"{bonus:+0;-0}";
        yield return new RollModifier("willpower", bonus, advantage, $"Willpower {will:0}: {effect} vs {tag}");
    }

    /// <summary>≥90 +1; 60–89 none; 30–59 −1; 10–29 −2; under 10 −3 and disadvantage.</summary>
    public static (int Bonus, AdvantageEffect Advantage) Band(float willpower) => willpower switch
    {
        >= 90 => (1, AdvantageEffect.None),
        >= 60 => (0, AdvantageEffect.None),
        >= 30 => (-1, AdvantageEffect.None),
        >= 10 => (-2, AdvantageEffect.None),
        _ => (-3, AdvantageEffect.Disadvantage),
    };
}
