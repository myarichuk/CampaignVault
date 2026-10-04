using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data;

/// <summary>
/// What the engine checks when a Spell action is committed: the slot spend is in the same batch (M-60), and the spell is one
/// the caster has recorded (M-61). It passes whenever the engine cannot know: no batch, a caster with no slot pools or an
/// empty repertoire, a spell with no definition (homebrew). The campaign option <see cref="OptionKey"/> set to "warn" turns
/// the refusals into messages.
/// </summary>
internal static class SpellBookkeeping
{
    public const string OptionKey = "spellBookkeeping";

    /// <summary>Action parameters that say the cast legitimately skips the slot or the repertoire: a ritual, an innate or racial spell, a focus spell, a scroll or wand.</summary>
    private static readonly string[] Exemptions = ["ritual", "innate", "focus", "item"];

    public static bool Warns(IReadOnlyDictionary<string, string>? options) =>
        options is not null
        && options.FirstOrDefault(o => string.Equals(o.Key, OptionKey, StringComparison.OrdinalIgnoreCase)).Value is { } v
        && string.Equals(v.Trim(), "warn", StringComparison.OrdinalIgnoreCase);

    public static bool IsExempt(RulesetAction action) =>
        Exemptions.Any(key => action.Parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) && on);

    public static string EscapeHint =>
        $"If this cast really needs no slot or isn't on their list (ritual, innate or racial spell, focus spell, scroll or wand), set the action parameter {string.Join(" or ", Exemptions.Select(e => $"{e}=true"))}. A table that wants this relaxed sets the campaign option {OptionKey}=warn.";

    /// <summary>M-60: a leveled spell needs a slot spend for the caster in the same batch. Returns the problem, or null.</summary>
    public static string? CheckSlotPairing(RulesetAction action, Character caster, SpellDefinition spell, IReadOnlyList<WorldChange>? batch)
    {
        if (batch is null || SpellSlotValidator.IsCantrip(spell) || IsExempt(action))
            return null;

        var pools = caster.SystemStats?.ResourcePools;
        if (pools is null || !pools.Keys.Any(k => k.StartsWith("spell_slots_", StringComparison.Ordinal)))
            return null; // a caster without slot pools (an NPC, a legacy sheet): nothing to pair with

        var paired = batch.OfType<ResourceChange>().Any(rc =>
            rc.Delta < 0
            && string.Equals(rc.CharacterId, caster.Id, StringComparison.OrdinalIgnoreCase)
            && (SpellSlotValidator.TryParseSlotLevel(rc.PoolName, out _) || rc.PoolName is "pact_magic" or "focus_points"));
        return paired
            ? null
            : $"{caster.Name} casts {spell.Name} (level {spell.Level}) but this batch has no spell-slot spend for them. "
              + $"Add a resource change on spell_slots_{spell.Level} (delta -1, spellName '{spell.Name}') to the same commit. {EscapeHint}";
    }

    /// <summary>M-61: the spell must be in the caster's recorded cantrips, prepared spells (prepared casters) or known spells. Returns the problem, or null.</summary>
    public static string? CheckRepertoire(RulesetAction action, Character caster, SpellDefinition spell)
    {
        if (IsExempt(action))
            return null;

        var spells = caster.SystemStats switch
        {
            Dnd5eExtension d => d.Spells,
            Pf2eExtension p => p.Spells,
            _ => null,
        };
        if (spells is null || (spells.Cantrips.Count == 0 && spells.Known.Count == 0 && spells.Prepared.Count == 0))
            return null; // nothing recorded: the engine cannot say what they know

        var slug = CastingComponentGate.NormalizeSlug(spell.Name);
        bool Has(List<string> list) => list.Any(s => CastingComponentGate.NormalizeSlug(s) == slug);

        if (SpellSlotValidator.IsCantrip(spell))
            return Has(spells.Cantrips) || Has(spells.Known) || Has(spells.Prepared)
                ? null
                : $"{caster.Name} doesn't have the cantrip {spell.Name} recorded. {EscapeHint}";

        if (spells.Prepared.Count > 0)
        {
            if (Has(spells.Prepared))
                return null;
            return Has(spells.Known)
                ? $"{caster.Name} knows {spell.Name} but hasn't prepared it. {EscapeHint}"
                : $"{caster.Name} hasn't prepared {spell.Name} and it isn't in their spellbook. {EscapeHint}";
        }

        return Has(spells.Known)
            ? null
            : $"{caster.Name} doesn't know {spell.Name}. {EscapeHint}";
    }

    /// <summary>M-62: a 5e concentration spell puts the Concentration marker on its caster. PF2e's data flags the "concentrate" trait with the same field, and PF2e sustains spells instead, so it is left out.</summary>
    public static bool AppliesConcentration(Character caster, SpellDefinition spell) =>
        spell.Concentration == true && caster.SystemStats is Dnd5eExtension;

    /// <summary>Whether the batch already adds a Concentration status to <paramref name="caster"/> (the old manual step), so the engine doesn't add a second.</summary>
    public static bool BatchAddsConcentration(IReadOnlyList<WorldChange>? batch, string casterId) =>
        batch?.OfType<StatusChange>().Any(sc =>
            string.Equals(sc.CharacterId, casterId, StringComparison.OrdinalIgnoreCase)
            && (sc.Effect is { } e ? ConcentrationRules.IsConcentration(e) : sc.Status.StartsWith("Concentration", StringComparison.OrdinalIgnoreCase))) == true;

    public static StatusEffect ConcentrationEffect(SpellDefinition spell) => new()
    {
        Name = $"Concentration: {spell.Name}",
        Category = "Concentration",
        ConditionName = ConcentrationRules.Marker,
        RecoveryHint = "Applied by the engine when the spell was cast; ends when concentration breaks.",
    };
}
