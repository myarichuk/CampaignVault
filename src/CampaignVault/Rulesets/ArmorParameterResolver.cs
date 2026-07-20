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
/// checking in EquipSlotRules already prevents two items in the same zone+layer+StackGroup pool, e.g.
/// two breastplates). Items in other layers (Base/Outer) only add if Properties["stacksWithArmor"] ==
/// "true" — this is how an enchanted robe can layer meaningfully over chainmail without every
/// cloak silently stacking with every cuirass. Shields (EquipLayer.Held in the OffHand zone)
/// always add. Warmth and movement modifiers (penalties/buffs) sum across every equipped item regardless
/// of layer (insulation and speed effects are cumulative, not a defensive stack).
///
/// Dex-cap source: driven by the primary body armor (Torso/Armor layer). With StackGroup enabling
/// multiple coexisting Torso/Armor pieces (modular armor), which piece governs the dex-cap can become
/// ambiguous — see ComputeContributions for the dexCapSource disambiguation rules.
/// </summary>
public static class ArmorParameterResolver
{
    /// <summary>Loads the character's equipped items via ChangeContext, applies the resolved AC/warmth/movement modifier, and records any diagnostic messages.</summary>
    public static async Task ApplyAsync(Character character, ChangeContext context, CancellationToken ct = default)
    {
        var equippedItems = await ItemHolderQueryHelper.GetEquippedItemsAsync(context, character.Id, ct: ct);
        var messages = Apply(character, equippedItems);
        foreach (var message in messages)
        {
            context.RecordMessage(message);
        }
    }

    /// <summary>
    /// Pure variant for callers that already have the equipped-item list (e.g. bootstrap steps).
    /// Returns any diagnostic messages (ENGINE WARNING / NARRATIVE PROMPT) produced while resolving
    /// dex-cap ambiguity; empty when nothing is ambiguous (the common case, byte-identical to prior behavior).
    /// </summary>
    public static List<string> Apply(Character character, IReadOnlyList<Item> equippedItems)
    {
        var (acBonus, dexCap, warmth, movementModifier, messages) = ComputeContributions(equippedItems);

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

        return messages;
    }

    private static (int AcBonus, int? DexCap, float Warmth, float MovementModifier, List<string> Messages) ComputeContributions(IReadOnlyList<Item> equippedItems)
    {
        var messages = new List<string>();
        var acBonus = 0;
        int? dexCap = null;
        var warmth = 0f;
        var movementModifier = 0f;

        var bodyArmor = ResolveDexCapSource(equippedItems, messages);
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
                acBonus += (int)Math.Round(ac, MidpointRounding.AwayFromZero);
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

        return (acBonus, dexCap, warmth, movementModifier, messages);
    }

    /// <summary>
    /// Picks which Torso/Armor item governs the dex-cap.
    /// - Zero or one Torso/Armor item equipped: today's exact behavior (FirstOrDefault), no message.
    /// - Multiple equipped, none marked Properties["dexCapSource"]="true": fall back to first-match,
    ///   emit a NARRATIVE PROMPT naming the ambiguity.
    /// - Exactly one marked: use it, no message.
    /// - Multiple marked: ENGINE WARNING, deterministic first-match tie-break.
    /// </summary>
    private static Item? ResolveDexCapSource(IReadOnlyList<Item> equippedItems, List<string> messages)
    {
        var torsoArmorItems = equippedItems
            .Where(i => i.EquipLayer == Models.EquipLayer.Armor && i.EquipZones.Contains(EquipZone.Torso))
            .ToList();

        if (torsoArmorItems.Count <= 1)
        {
            return torsoArmorItems.FirstOrDefault();
        }

        var marked = torsoArmorItems
            .Where(i => TryGetProperty(i, "dexCapSource", out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (marked.Count == 1)
        {
            return marked[0];
        }

        if (marked.Count > 1)
        {
            var chosen = marked[0];
            var names = string.Join(", ", marked.Select(i => $"'{i.Name}' ({i.Id})"));
            messages.Add(
                $"ENGINE WARNING: Multiple equipped Torso/Armor items are marked dexCapSource:true ({names}) — " +
                $"using '{chosen.Name}' for the dex-cap (first-match tie-break). Clear the extra dexCapSource flags via world_build/item_update to remove this ambiguity.");
            return chosen;
        }

        var fallback = torsoArmorItems[0];
        var candidateNames = string.Join(", ", torsoArmorItems.Select(i => $"'{i.Name}' ({i.Id})"));
        messages.Add(
            $"NARRATIVE PROMPT: Multiple Torso/Armor items are equipped ({candidateNames}) with no dexCapSource marker — " +
            $"using '{fallback.Name}' for the dex-cap by default. Set Properties[\"dexCapSource\"]=\"true\" on the piece that should govern it via world_build/item_update.");
        return fallback;
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
