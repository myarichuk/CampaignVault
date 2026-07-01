using CampaignVault.Data.Templates;

namespace CampaignVault.Data;

/// <summary>
/// Validates spell slot expenditure against <see cref="SpellDefinition"/> metadata.
/// </summary>
public static class SpellSlotValidator
{
    public static bool TryParseSlotLevel(string poolName, out int slotLevel)
    {
        slotLevel = 0;
        const string prefix = "spell_slots_";
        if (!poolName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return int.TryParse(poolName[prefix.Length..], out slotLevel);
    }

    public static bool IsSpellSlotSpend(int delta, string poolName) =>
        delta < 0 && TryParseSlotLevel(poolName, out _);

    public static bool IsCantrip(SpellDefinition spell) => (spell.Level ?? 0) == 0;

    public static string CantripWarning(SpellDefinition spell) =>
        $"[WARNING] '{spell.Name}' is a cantrip and does not expend spell slots in D&D 5e / PF2e — " +
        "spend was applied, but slot-level validation was skipped.";

    /// <summary>
    /// Returns an error message when spend is invalid; null when valid or not applicable.
    /// Cantrips are handled separately via <see cref="IsCantrip"/> / <see cref="CantripWarning"/>
    /// as a soft warning, consistent with the other spell-validation paths.
    /// </summary>
    public static string? ValidateSpend(SpellDefinition? spell, int slotLevel)
    {
        if (spell == null || IsCantrip(spell))
            return null;

        var spellLevel = spell.Level ?? 0;

        if (spellLevel > slotLevel)
        {
            return $"Spell '{spell.Name}' is level {spellLevel} but pool 'spell_slots_{slotLevel}' is only level {slotLevel}.";
        }

        return null;
    }

    public static string? BuildConcentrationHint(SpellDefinition spell) =>
        spell.Concentration == true
            ? $"[HINT] '{spell.Name}' requires concentration — commit a separate status change after the cast."
            : null;
}