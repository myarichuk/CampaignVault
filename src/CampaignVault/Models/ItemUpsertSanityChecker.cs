namespace CampaignVault.Models;

/// <summary>
/// Advisory (non-blocking) sanity checks for equip-related fields on an item-upsert request. Mirrors
/// this codebase's existing precedent (warmth/speedModifier) of nudging rather than hard-blocking
/// ambiguous-but-not-broken configurations. Pure, no DB access.
/// </summary>
public static class ItemUpsertSanityChecker
{
    private static readonly HashSet<string> RecognizedDefenseKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "acBonus", "warmth", "speedModifier", "armorType", "dexCap", "dexCapSource", "stacksWithArmor",
        };

    public static List<string> GetNudges(ItemUpsertRequest item, bool itemAlreadyExisted = false, bool definitionNameUnresolved = false)
    {
        var nudges = new List<string>();
        var zones = item.EquipZones;
        var layer = item.EquipLayer;

        if (itemAlreadyExisted && item.ItemDetails is { Count: > 0 })
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' already existed — the itemDetails you supplied were ignored " +
                "(itemDetails only seeds a NEW item). Use commit's item_update/upsertItemDetail to modify an existing item's details.");
        }

        if (definitionNameUnresolved)
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' set definitionName:'{item.DefinitionName}' but no matching ItemDefinition " +
                "was found for the campaign's active system — the item was created from your explicit fields only. " +
                "Check the name via get_rules_reference (kind:'items'), or ignore if a custom item was intended.");
        }

        if (item.TwoHanded == true && (zones == null || !zones.Contains(EquipZones.MainHand, StringComparer.OrdinalIgnoreCase)))
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' has twoHanded:true but its equipZones don't include MainHand — " +
                "TwoHanded only has an effect on a MainHand item (it also blocks OffHand). Add MainHand to equipZones, or clear twoHanded if unintended.");
        }

        if (zones is { Count: > 0 } && zones.Contains(EquipZones.MainHand, StringComparer.OrdinalIgnoreCase)
            && zones.Any(z => !string.Equals(z, EquipZones.MainHand, StringComparison.OrdinalIgnoreCase) && !string.Equals(z, EquipZones.OffHand, StringComparison.OrdinalIgnoreCase)))
        {
            var bodyZones = string.Join(", ", zones.Where(z => !string.Equals(z, EquipZones.MainHand, StringComparison.OrdinalIgnoreCase) && !string.Equals(z, EquipZones.OffHand, StringComparison.OrdinalIgnoreCase)));
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' combines MainHand with body-slot zone(s) ({bodyZones}) on the same item — " +
                "this is unusual (a held weapon sharing a zone list with worn gear). Confirm this is intended.");
        }

        if (!string.IsNullOrWhiteSpace(item.StackGroup) && ((zones == null || zones.Count == 0) || layer == null))
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' has StackGroup set but no EquipZones/EquipLayer — " +
                "StackGroup only matters once the item is equippable. Set equipZones/equipLayer, or clear StackGroup if unintended.");
        }

        if ((item.CoreCategory == ItemCategories.Armor || layer == EquipLayers.Held)
            && item.Properties is { Count: > 0 }
            && !RecognizedDefenseKeys.Overlaps(item.Properties.Keys))
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' is Armor/Held-slot but its Properties use none of the recognized defense keys " +
                "(acBonus, warmth, speedModifier, armorType, dexCap, dexCapSource, stacksWithArmor) — if this item is meant to " +
                "affect AC/warmth/movement, check the key names via get_help; otherwise ignore.");
        }

        return nudges;
    }
}
