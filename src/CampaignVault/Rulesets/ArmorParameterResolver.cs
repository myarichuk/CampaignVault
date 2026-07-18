using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Recomputes a character's ArmorClass, WarmthRating, and MovementModifier from all currently-equipped items.
/// Called synchronously from item_equip/item_unequip (primary path), item_update (when an
/// already-worn item's relevant properties change), and bootstrap defense steps (pre-equipped
/// starting gear). Shape cloned from WeaponParameterResolver.cs.
///
/// Layering combine rule: within EquipLayer.Armor, every equipped item's acBonus counts (conflict
/// checking in EquipSlotRules already prevents two items in the same zone+layer, e.g. two
/// breastplates). Items in other layers (Base/Outer) only add if Properties["stacksWithArmor"] ==
/// "true" — this is how an enchanted robe can layer meaningfully over chainmail without every
/// cloak silently stacking with every cuirass. Shields (EquipLayer.Held in the OffHand zone)
/// always add. Warmth and movement modifiers (penalties/buffs) sum across every equipped item regardless
/// of layer (insulation and speed effects are cumulative, not a defensive stack).
/// </summary>
public static class ArmorParameterResolver
{
    /// <summary>Loads the character's equipped items via ChangeContext and applies the resolved AC/warmth.</summary>
    public static async Task ApplyAsync(Character character, ChangeContext context, CancellationToken ct = default)
    {
        var equippedItems = await ItemHolderQueryHelper.GetEquippedItemsAsync(context, character.Id, ct: ct);
        Apply(character, equippedItems);
    }

    /// <summary>Pure variant for callers that already have the equipped-item list (e.g. bootstrap steps).</summary>
    public static void Apply(Character character, IReadOnlyList<Item> equippedItems)
    {
        var (acBonus, dexCap, warmth, movementModifier) = ComputeContributions(equippedItems);

        switch (character.SystemStats)
        {
            case Dnd5eExtension dnd5e:
            {
                var dexMod = dnd5e.GetAbilityModifier(dnd5e.Dexterity);
                var effectiveDex = dexCap.HasValue ? Math.Min(dexMod, dexCap.Value) : dexMod;
                dnd5e.ArmorClass = 10 + effectiveDex + acBonus;
                break;
            }
            case Pf2eExtension pf2e:
            {
                var proficiencyBonus = pf2e.AcProficiency == Pf2eProficiencyRank.Untrained || !pf2e.Level.HasValue
                    ? 0
                    : pf2e.Level.Value + (int)pf2e.AcProficiency;
                var effectiveDex = dexCap.HasValue ? Math.Min(pf2e.DexterityMod, dexCap.Value) : pf2e.DexterityMod;
                pf2e.ArmorClass = 10 + effectiveDex + proficiencyBonus + acBonus;
                break;
            }
        }

        character.SystemStats.WarmthRating = warmth;
        character.SystemStats.MovementModifier = movementModifier;
    }

    private static (int AcBonus, int? DexCap, float Warmth, float MovementModifier) ComputeContributions(IReadOnlyList<Item> equippedItems)
    {
        var acBonus = 0;
        int? dexCap = null;
        var warmth = 0f;
        var movementModifier = 0f;

        // Dex-cap: driven by the primary body armor (Torso/Armor layer).
        // - 5e: light = uncapped, medium = +2 max, heavy = +0 (via "armorType" property)
        // - PF2e: explicit numeric dexCap property overrides armorType mapping; treat armorType as 5e fallback.
        // Unrecognized/absent armorType is treated as uncapped.
        var bodyArmor = equippedItems.FirstOrDefault(i =>
            i.EquipLayer == Models.EquipLayer.Armor && i.EquipZones.Contains(EquipZone.Torso));
        if (bodyArmor != null)
        {
            // Check for explicit numeric dexCap (PF2e style) first
            if (TryGetProperty(bodyArmor, "dexCap", out var dexCapRaw) && int.TryParse(dexCapRaw, out var dexCapVal))
            {
                dexCap = dexCapVal;
            }
            // Fallback to 5e armorType mapping
            else if (TryGetProperty(bodyArmor, "armorType", out var armorType))
            {
                dexCap = armorType.Trim().ToLowerInvariant() switch
                {
                    "medium" => 2,
                    "heavy" => 0,
                    _ => (int?)null,
                };
            }
        }

        foreach (var item in equippedItems)
        {
            var isShield = item.EquipLayer == Models.EquipLayer.Held && item.EquipZones.Contains(EquipZone.OffHand);
            var stacksWithArmor = TryGetProperty(item, "stacksWithArmor", out var stacksRaw)
                                   && stacksRaw.Equals("true", StringComparison.OrdinalIgnoreCase);

            var contributesAc = item.EquipLayer == Models.EquipLayer.Armor || isShield || stacksWithArmor;

            if (contributesAc && TryGetProperty(item, "acBonus", out var acRaw) && float.TryParse(acRaw, out var ac))
            {
                acBonus += (int)ac;
            }

            if (TryGetProperty(item, "warmth", out var warmthRaw) && float.TryParse(warmthRaw, out var w))
            {
                warmth += w;
            }

            if (TryGetProperty(item, "speedModifier", out var speedModifierRaw) && float.TryParse(speedModifierRaw, out var sm))
            {
                movementModifier += sm;
            }
        }

        return (acBonus, dexCap, warmth, movementModifier);
    }

    private static bool TryGetProperty(Item item, string key, out string value)
    {
        if (item.Properties.TryGetValue(key, out var raw) && raw != null)
        {
            var str = raw.ToString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                value = str;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
