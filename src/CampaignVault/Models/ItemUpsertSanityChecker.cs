namespace CampaignVault.Models;

/// <summary>
/// Advisory (non-blocking) sanity checks for equip-related fields on an item-upsert request. Mirrors
/// this codebase's existing precedent (warmth/speedModifier) of nudging rather than hard-blocking
/// ambiguous-but-not-broken configurations. Pure, no DB access.
/// </summary>
public static class ItemUpsertSanityChecker
{
    public static List<string> GetNudges(ItemUpsertRequest item, bool itemAlreadyExisted = false)
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

        if (item.TwoHanded == true && (zones == null || !zones.Contains(EquipZone.MainHand)))
        {
            nudges.Add(
                $"NARRATIVE PROMPT: '{item.Id}' has twoHanded:true but its equipZones don't include MainHand — " +
                "TwoHanded only has an effect on a MainHand item (it also blocks OffHand). Add MainHand to equipZones, or clear twoHanded if unintended.");
        }

        if (zones is { Count: > 0 } && zones.Contains(EquipZone.MainHand)
            && zones.Any(z => z != EquipZone.MainHand && z != EquipZone.OffHand))
        {
            var bodyZones = string.Join(", ", zones.Where(z => z != EquipZone.MainHand && z != EquipZone.OffHand));
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

        return nudges;
    }
}
