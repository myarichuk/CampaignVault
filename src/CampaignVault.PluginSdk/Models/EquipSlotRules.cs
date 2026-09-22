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

    /// <summary>Max simultaneous items (within the same layer+StackGroup pool) a zone can hold. Default 1.</summary>
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
    /// Per-zone conflict detail: which zone/layer/StackGroup pool is contested, its capacity, how many
    /// items currently occupy it, and which already-equipped item(s) would need to be freed.
    /// </summary>
    public sealed record ZoneConflict(EquipZone Zone, EquipLayer Layer, string? StackGroup, int Capacity, int Occupied, IReadOnlyList<Item> ToFree);

    /// <summary>
    /// Result of <see cref="FindConflicts"/>: the flattened, de-duplicated set of items that must be
    /// freed (convenience for callers that only need "what to unequip"), plus the structured per-zone
    /// breakdown used for diagnostic messages.
    /// </summary>
    public sealed class ConflictResult(List<Item> items, List<ZoneConflict> zones)
    {
        public List<Item> Items { get; } = items;
        public List<ZoneConflict> Zones { get; } = zones;
        public bool HasConflicts => Items.Count > 0;
    }

    /// <summary>
    /// Returns the minimal set of already-equipped items that must be unequipped to make room for
    /// <paramref name="candidate"/>, or an empty result if it can be equipped as-is.
    ///
    /// Items are grouped by (zone, layer, StackGroup) — this is what lets Torso/Armor + Torso/Outer +
    /// Torso/Base coexist (different layers), and what lets two differently-StackGrouped items coexist
    /// on the same zone+layer (modular pauldrons). Both-null StackGroup counts as equal, so ungrouped
    /// items keep today's exact flat zone+layer behavior; a StackGroup-tagged item is a separate pool
    /// from an ungrouped item on the same zone+layer and never conflicts with it.
    ///
    /// When a capacity>1 zone (Ring, Accessory) must free some but not all occupants, the oldest-equipped
    /// (lowest LastUpdated) occupant(s) are freed first.
    /// </summary>
    public static ConflictResult FindConflicts(Item candidate, IEnumerable<Item> alreadyEquipped)
    {
        if (candidate.EquipZones.Count == 0 || candidate.EquipLayer == null)
        {
            return new ConflictResult([], []);
        }

        var equippedList = alreadyEquipped
            .Where(i => !i.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var allConflicts = new List<Item>();
        var zoneConflicts = new List<ZoneConflict>();

        foreach (var zone in GetEffectiveZones(candidate))
        {
            var capacity = GetCapacity(zone);
            var occupying = equippedList
                .Where(i => i.EquipLayer == candidate.EquipLayer
                            && string.Equals(i.StackGroup, candidate.StackGroup, StringComparison.OrdinalIgnoreCase)
                            && GetEffectiveZones(i).Contains(zone))
                .Where(i => allConflicts.All(c => c.Id != i.Id))
                .ToList();

            var needToFree = occupying.Count - capacity + 1;
            if (needToFree > 0)
            {
                var toFree = occupying
                    .OrderBy(i => i.LastUpdated)
                    .Take(needToFree)
                    .ToList();
                allConflicts.AddRange(toFree);
                zoneConflicts.Add(new ZoneConflict(zone, candidate.EquipLayer.Value, candidate.StackGroup, capacity, occupying.Count, toFree));
            }
        }

        return new ConflictResult(allConflicts, zoneConflicts);
    }

    /// <summary>One tag from <see cref="Item.IncompatibleWithEquippedTags"/> found on an already-equipped item.</summary>
    public sealed record TagIncompatibility(string Tag, Item ConflictingItem);

    /// <summary>
    /// Result of <see cref="FindTagIncompatibilities"/>: missing prerequisite tags (from
    /// RequiresEquippedTags, none currently equipped) and present incompatibilities (from
    /// IncompatibleWithEquippedTags, matched against a currently-equipped item).
    /// </summary>
    public sealed class TagCheckResult(List<string> missingPrerequisiteTags, List<TagIncompatibility> incompatibilities)
    {
        public List<string> MissingPrerequisiteTags { get; } = missingPrerequisiteTags;
        public List<TagIncompatibility> Incompatibilities { get; } = incompatibilities;
        public bool HasIssues => MissingPrerequisiteTags.Count > 0 || Incompatibilities.Count > 0;
    }

    /// <summary>
    /// Zone/layer-independent prerequisite and incompatibility checks driven by
    /// <see cref="Item.RequiresEquippedTags"/> / <see cref="Item.IncompatibleWithEquippedTags"/> against
    /// <see cref="Item.Tags"/> on already-equipped items. Strict no-op when both lists are null/empty on
    /// the candidate (true for every item until a DM opts in via world_build).
    /// </summary>
    public static TagCheckResult FindTagIncompatibilities(Item candidate, IEnumerable<Item> alreadyEquipped)
    {
        var equippedList = alreadyEquipped
            .Where(i => !i.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missing = new List<string>();
        if (candidate.RequiresEquippedTags is { Count: > 0 })
        {
            foreach (var tag in candidate.RequiresEquippedTags)
            {
                var satisfied = equippedList.Any(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                if (!satisfied)
                {
                    missing.Add(tag);
                }
            }
        }

        var incompatibilities = new List<TagIncompatibility>();
        if (candidate.IncompatibleWithEquippedTags is { Count: > 0 })
        {
            foreach (var equipped in equippedList)
            {
                foreach (var tag in candidate.IncompatibleWithEquippedTags)
                {
                    if (equipped.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        incompatibilities.Add(new TagIncompatibility(tag, equipped));
                    }
                }
            }
        }

        return new TagCheckResult(missing, incompatibilities);
    }
}
