using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Rulesets;

/// <summary>
/// What a character's own effects (class features, race, feats: <see cref="FeatEffectRules.DataEffects"/>) add to its
/// initiative (<c>initiativeBonus</c>) and speed (<c>speedBonus</c>, feet). The other effect kinds are folded where their
/// roll is made, with the action's toggles and assertions; these two have no action, so only unconditional effects count.
/// </summary>
internal sealed class CharacterEffectModifierProvider(
    FeatDefinitionProvider? feats = null,
    ProgressionDefinitionProvider? progressions = null,
    RaceDefinitionProvider? races = null) : IRollModifierProvider
{
    public IEnumerable<RollModifier> Modifiers(RollQuery query)
    {
        var kind = query.Kind switch
        {
            RollKinds.Initiative => FeatEffectKinds.InitiativeBonus,
            RollKinds.Speed => FeatEffectKinds.SpeedBonus,
            _ => null,
        };
        if (kind is null || query.System is null || query.Actor.SystemStats is null)
            yield break;

        var (total, reasons) = FeatEffectRules.Sum(FeatEffectRules.DataEffects(query.Actor, query.System, feats, progressions, races), kind);
        if (total != 0)
            yield return new RollModifier("effects", total, AdvantageEffect.None, string.Join("; ", reasons));
    }
}
