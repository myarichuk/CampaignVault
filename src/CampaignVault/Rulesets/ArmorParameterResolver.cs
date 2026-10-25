using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Services;

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
    public static async Task ApplyAsync(
        Character character, ChangeContext context, CancellationToken ct = default, ProgressionDefinitionProvider? progressions = null)
    {
        character.LastUpdated = DateTime.UtcNow;
        var equippedItems = await ItemHolderQueryHelper.GetEquippedItemsAsync(context, character.Id, ct: ct);
        var messages = Apply(character, equippedItems, progressions);
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
    /// <summary>Status effects the resolver itself owns (armor's stealth and check penalties); rebuilt on every call.</summary>
    public const string StealthPenaltyEffectKey = "engine:armor-stealth";
    public const string CheckPenaltyEffectKey = "engine:armor-check";

    /// <summary>The dnd5e condition (RulesetData/dnd5e/conditions/noisy_armor.yaml) that gives disadvantage on Stealth.</summary>
    public const string NoisyArmorCondition = "noisy_armor";

    /// <summary>PF2e armor check penalty applies to Strength- and Dexterity-based skill checks.</summary>
    private static readonly string[] Pf2eCheckPenaltySkills = ["Acrobatics", "Athletics", "Stealth", "Thievery"];

    public static List<string> Apply(
        Character character, IReadOnlyList<Item> equippedItems, ProgressionDefinitionProvider? progressions = null)
    {
        // PF2e: a shield gives AC only while raised (Raise a Shield puts a status effect on the bearer); it never sits in the static AC.
        var shieldsContribute = character.SystemStats is not Pf2eExtension;
        var (acBonus, dexCap, warmth, movementModifier, messages) = ComputeContributions(equippedItems, shieldsContribute);
        var wearsBodyArmor = equippedItems.Any(IsBodyArmor);
        var carriesShield = equippedItems.Any(IsShield);
        var bodyArmor = equippedItems.FirstOrDefault(IsBodyArmor);

        if (character.SystemStats is not null)
        {
            character.SystemStats.StatusEffects ??= [];
            character.SystemStats.StatusEffects.RemoveAll(e => e.EffectKey is StealthPenaltyEffectKey or CheckPenaltyEffectKey);
        }

        switch (character.SystemStats)
        {
            case Dnd5eExtension dnd5e:
            {
                var dexMod = dnd5e.GetAbilityModifier(dnd5e.Dexterity);
                var effectiveDex = dexCap.HasValue ? Math.Min(dexMod, dexCap.Value) : dexMod;

                // SRD: heavy armor with a Strength minimum the wearer doesn't meet costs 10 feet of speed.
                if (bodyArmor is not null && TryGetProperty(bodyArmor, "strMinimum", out var strMinRaw)
                    && TryParseInt(strMinRaw, out var strMin) && dnd5e.Strength < strMin)
                {
                    movementModifier -= 10;
                    messages.Add($"{character.Name}'s Strength {dnd5e.Strength} is below {bodyArmor.Name}'s minimum of {strMin}: speed reduced by 10 ft.");
                }

                // SRD: armor that imposes disadvantage on Stealth does so through a condition the roll pipeline reads.
                var noisy = equippedItems.FirstOrDefault(i => TryGetProperty(i, "stealthDisadvantage", out var sd) && sd.Equals("true", StringComparison.OrdinalIgnoreCase));
                if (noisy is not null)
                {
                    dnd5e.StatusEffects.Add(new StatusEffect
                    {
                        Name = "Noisy Armor",
                        Category = "Condition",
                        ConditionName = NoisyArmorCondition,
                        EffectKey = StealthPenaltyEffectKey,
                        AppliedBy = "system/armor",
                        RecoveryHint = $"Disadvantage on Stealth checks while wearing {noisy.Name}.",
                    });
                }

                if (wearsBodyArmor)
                {
                    dnd5e.ArmorClass = 10 + effectiveDex + acBonus;
                    break;
                }

                // No body armor: the best of 10 + Dexterity, a class feature's formula (Unarmored Defense) and the stat block's
                // own AC; a shield and other AC items add on top.
                var feature = CharacterClassFeatures.UnarmoredArmorClass(
                    character, RulesetSystem.Dnd5e, progressions, a => dnd5e.GetAbilityModifier(Dnd5eExtension.AbilityScore(dnd5e, a)), carriesShield);
                dnd5e.ArmorClass = Math.Max(10 + dexMod, Math.Max(feature ?? 0, dnd5e.BaseArmorClass ?? 0)) + acBonus;
                break;
            }
            case Pf2eExtension pf2e:
            {
                var proficiencyBonus = pf2e.AcProficiency == Pf2eProficiencyRank.Untrained || !pf2e.Level.HasValue
                    ? 0
                    : pf2e.Level.Value + (int)pf2e.AcProficiency;
                var effectiveDex = dexCap.HasValue ? Math.Min(pf2e.DexterityMod, dexCap.Value) : pf2e.DexterityMod;
                var derived = 10 + effectiveDex + proficiencyBonus;
                pf2e.ArmorClass = (wearsBodyArmor ? derived : Math.Max(derived, pf2e.BaseArmorClass ?? 0)) + acBonus;

                // Player Core: the armor's check penalty applies to Strength- and Dexterity-based skills, and its speed penalty in
                // full, unless the wearer's Strength modifier meets the armor's Strength value: then no check penalty and the
                // speed penalty is 5 feet lighter.
                if (bodyArmor is not null)
                {
                    var meetsStrength = TryGetProperty(bodyArmor, "strength", out var strRaw) && TryParseInt(strRaw, out var strNeed) && pf2e.StrengthMod >= strNeed;
                    var checkPenalty = TryGetProperty(bodyArmor, "checkPenalty", out var cpRaw) && TryParseInt(cpRaw, out var cp) ? cp : 0;
                    if (checkPenalty != 0 && !meetsStrength)
                    {
                        pf2e.StatusEffects.Add(new StatusEffect
                        {
                            Name = "Armor Check Penalty",
                            Category = "Condition",
                            EffectKey = CheckPenaltyEffectKey,
                            AppliedBy = "system/armor",
                            StatModifiers = Pf2eCheckPenaltySkills.ToDictionary(s => s, _ => (float)checkPenalty),
                            RecoveryHint = $"{checkPenalty} to Acrobatics, Athletics, Stealth and Thievery while wearing {bodyArmor.Name}.",
                        });
                    }

                    if (meetsStrength && movementModifier < 0)
                    {
                        movementModifier = Math.Min(0f, movementModifier + 5f);
                    }
                }

                break;
            }
        }

        character.SystemStats.WarmthRating = warmth;
        character.SystemStats.MovementModifier = movementModifier;

        return messages;
    }

    private static (int AcBonus, int? DexCap, float Warmth, float MovementModifier, List<string> Messages) ComputeContributions(
        IReadOnlyList<Item> equippedItems, bool shieldsContribute = true)
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
            if (TryGetProperty(bodyArmor, "dexCap", out var dexCapRaw) && TryParseInt(dexCapRaw, out var dexCapVal))
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
            var isShield = IsShield(item);
            var stacksWithArmor = TryGetProperty(item, "stacksWithArmor", out var stacksRaw)
                                   && stacksRaw.Equals("true", StringComparison.OrdinalIgnoreCase);

            var contributesAc = item.EquipLayer == EquipLayers.Armor || (isShield && shieldsContribute) || stacksWithArmor;

            if (contributesAc && TryGetProperty(item, "acBonus", out var acRaw) && TryParseFloat(acRaw, out var ac))
            {
                acBonus += (int)Math.Round(ac, MidpointRounding.AwayFromZero);
            }

            if (TryGetProperty(item, "warmth", out var warmthRaw) && TryParseFloat(warmthRaw, out var w))
            {
                warmth += w;
            }

            if (TryGetProperty(item, "speedModifier", out var speedModifierRaw) && TryParseFloat(speedModifierRaw, out var sm))
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
            .Where(i => i.EquipLayer == EquipLayers.Armor && i.EquipZones.Contains(EquipZones.Torso, StringComparer.OrdinalIgnoreCase))
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

    private static bool IsShield(Item item) =>
        item.EquipLayer == EquipLayers.Held && item.EquipZones.Contains(EquipZones.OffHand, StringComparer.OrdinalIgnoreCase);

    private static bool IsBodyArmor(Item item) =>
        item.EquipLayer == EquipLayers.Armor && item.EquipZones.Contains(EquipZones.Torso, StringComparer.OrdinalIgnoreCase);

    /// <summary>Shipped YAML is invariant ("-10.0"); a value a client wrote under its own culture ("‎-10") still parses.</summary>
    private static bool TryParseInt(string raw, out int value) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value)
        || int.TryParse(raw, out value);

    private static bool TryParseFloat(string raw, out float value) =>
        float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
        || float.TryParse(raw, out value);

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
