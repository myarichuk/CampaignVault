namespace CampaignVault.Models;

/// <summary>
/// Pure, in-memory conflict detection for equipping items into zones/layers. No DB access —
/// callers (ItemEquipHandler) are responsible for gathering the set of currently-equipped items.
/// </summary>
public static class EquipSlotRules
{
    private static readonly Dictionary<EquipZone, int> ZoneCapacityOverrides = new()
    {
        [EquipZone.Ring] = 2,
        [EquipZone.Accessory] = 4,
    };

    /// <summary>Max simultaneous items (within the same layer) a zone can hold. Default 1.</summary>
    public static int GetCapacity(EquipZone zone) => ZoneCapacityOverrides.GetValueOrDefault(zone, 1);

    /// <summary>
    /// The zones an item occupies once equipped, including the implicit OffHand occupation of a
    /// two-handed MainHand item.
    /// </summary>
    public static IReadOnlyList<EquipZone> GetEffectiveZones(Item item)
    {
        if (item.TwoHanded && item.EquipZones.Contains(EquipZone.MainHand) && !item.EquipZones.Contains(EquipZone.OffHand))
        {
            var zones = new List<EquipZone>(item.EquipZones) { EquipZone.OffHand };
            return zones;
        }

        return item.EquipZones;
    }

    /// <summary>
    /// Returns the minimal set of already-equipped items that must be unequipped to make room for
    /// <paramref name="candidate"/>, or an empty list if it can be equipped as-is. Only items in the
    /// same layer occupying an effective zone the candidate needs are considered (this is what lets
    /// Torso/Armor + Torso/Outer + Torso/Base coexist).
    /// </summary>
    public static List<Item> FindConflicts(Item candidate, IEnumerable<Item> alreadyEquipped)
    {
        if (candidate.EquipZones.Count == 0 || candidate.EquipLayer == null)
        {
            return [];
        }

        var equippedList = alreadyEquipped
            .Where(i => !i.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var conflicts = new List<Item>();
        foreach (var zone in GetEffectiveZones(candidate))
        {
            var capacity = GetCapacity(zone);
            var occupying = equippedList
                .Where(i => i.EquipLayer == candidate.EquipLayer && GetEffectiveZones(i).Contains(zone))
                .Where(i => conflicts.All(c => c.Id != i.Id))
                .ToList();

            var needToFree = occupying.Count - capacity + 1;
            if (needToFree > 0)
            {
                conflicts.AddRange(occupying.Take(needToFree));
            }
        }

        return conflicts;
    }
}
