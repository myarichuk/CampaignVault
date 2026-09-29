using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>What the resolver can tell about the weapon from the item's tags, the action category and the range parameter.</summary>
internal readonly record struct WeaponProfile(bool Known, bool Ranged, bool Finesse, bool TwoHanded, bool Heavy, bool Light = false)
{
    public static WeaponProfile Read(RulesetAction action)
    {
        var tags = action.Parameters.TryGetValue("weaponTags", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(CombatFeatureRules.Norm).ToHashSet()
            : [];

        var ranged = tags.Contains("ranged")
            || (tags.Count == 0 && action.ActionCategory == ActionCategory.Ranged)
            || (tags.Count == 0 && action.Parameters.TryGetValue("range", out var range)
                && range is "Near" or "Far" or "Distant");
        var known = tags.Count > 0 || ranged;
        return new WeaponProfile(known, ranged, tags.Contains("finesse"), tags.Contains("twohanded"), tags.Contains("heavy"), tags.Contains("light"));
    }
}

/// <summary>
/// To-hit and damage modifiers a weapon attack earns from the sheet when the caller does not state them. An explicit
/// <c>bonus</c> / <c>damageBonus</c> replaces the sheet-derived part entirely (the caller owns that number); per-attack
/// opt-ins such as <c>powerAttack</c> still apply on top, because they are a choice made for this attack.
/// </summary>
internal sealed record DerivedAttack(int ToHit, int Damage, string Note)
{
    /// <summary>"1d8+3": the caller already folded a flat modifier into the dice, so the sheet's must not be added again.</summary>
    public static bool HasInlineModifier(string? damageDice) =>
        damageDice is not null && System.Text.RegularExpressions.Regex.IsMatch(damageDice, @"\d\s*[+-]\s*\d+\s*$");

    public static DerivedAttack Dnd5e(
        Character actor, Dnd5eExtension stats, RulesetAction action, bool explicitBonus, bool explicitDamageBonus)
    {
        var weapon = WeaponProfile.Read(action);
        var str = stats.GetAbilityModifier(stats.Strength);
        var dex = stats.GetAbilityModifier(stats.Dexterity);
        var (mod, label) = weapon.Ranged ? (dex, "DEX")
            : weapon.Finesse ? (Math.Max(str, dex), dex >= str ? "DEX" : "STR")
            : !weapon.Known ? (Math.Max(str, dex), "best of STR/DEX, weapon type unknown")
            : (str, "STR");

        var prof = CombatFeatureRules.ProficiencyBonus(stats);

        var toHit = 0;
        var damage = 0;
        var hitParts = new List<string>();
        var dmgParts = new List<string>();
        var notes = new List<string>();

        if (!explicitBonus)
        {
            toHit += mod + prof;
            hitParts.Add($"{label} {Signed(mod)}");
            if (prof != 0) hitParts.Add($"prof {Signed(prof)}");
            else notes.Add("no proficiencyBonus on the sheet (stat-block creatures should pass bonus)");

            if (action.Parameters.TryGetValue("itemToHitBonus", out var item) && int.TryParse(item, out var itemBonus) && itemBonus != 0)
            {
                toHit += itemBonus;
                hitParts.Add($"weapon {Signed(itemBonus)}");
            }

            if (weapon.Ranged && CombatFeatureRules.HasFightingStyle(stats, "archery"))
            {
                toHit += 2;
                hitParts.Add("Archery +2");
            }
        }

        if (!explicitDamageBonus)
        {
            // Off-hand attack: no ability modifier on damage unless it is negative, or Two-Weapon Fighting.
            var offHand = CombatFeatureRules.IsTrue(action, "offHand");
            var damageMod = offHand && mod > 0 && !CombatFeatureRules.HasFightingStyle(stats, "twoWeaponFighting") ? 0 : mod;
            damage += damageMod;
            dmgParts.Add(damageMod != mod ? $"{label} not added (off-hand)" : $"{label} {Signed(mod)}");
            if (offHand && weapon.Known && !weapon.Light)
                notes.Add("off-hand attacks need a light melee weapon in each hand");
            if (!weapon.Ranged && !weapon.TwoHanded && weapon.Known && CombatFeatureRules.HasFightingStyle(stats, "dueling"))
            {
                damage += 2;
                dmgParts.Add("Dueling +2");
            }
        }

        return new DerivedAttack(toHit, damage, Describe(explicitBonus, explicitDamageBonus, hitParts, toHit, dmgParts, damage, notes));
    }

    public static DerivedAttack Pf2e(
        Character actor, Pf2eExtension stats, RulesetAction action, bool explicitBonus, bool explicitDamageBonus)
    {
        var weapon = WeaponProfile.Read(action);
        var str = stats.StrengthMod;
        var dex = stats.DexterityMod;
        var (mod, label) = weapon.Ranged ? (dex, "DEX")
            : weapon.Finesse ? (Math.Max(str, dex), dex >= str ? "DEX" : "STR")
            : !weapon.Known ? (Math.Max(str, dex), "best of STR/DEX, weapon type unknown")
            : (str, "STR");

        var toHit = 0;
        var damage = 0;
        var hitParts = new List<string>();
        var dmgParts = new List<string>();
        var notes = new List<string>();

        if (!explicitBonus)
        {
            toHit += mod;
            hitParts.Add($"{label} {Signed(mod)}");
            if (stats.Level is > 0)
            {
                var rank = stats.Attributes.TryGetValue("weaponProficiencyRank", out var r) ? (int)r : (int)Pf2eProficiencyRank.Trained;
                var proficiency = stats.Level.Value + rank;
                toHit += proficiency;
                hitParts.Add($"level+rank {Signed(proficiency)}");
                if (!stats.Attributes.ContainsKey("weaponProficiencyRank"))
                    notes.Add("assumes Trained weapon proficiency; set attributes.weaponProficiencyRank (2/4/6/8) to change");
            }
            else
            {
                notes.Add("no level on the sheet (stat-block creatures should pass bonus)");
            }

            if (action.Parameters.TryGetValue("itemToHitBonus", out var item) && int.TryParse(item, out var itemBonus) && itemBonus != 0)
            {
                toHit += itemBonus;
                hitParts.Add($"item {Signed(itemBonus)}");
            }
        }

        if (!explicitDamageBonus && !weapon.Ranged)
        {
            damage += str;
            dmgParts.Add($"STR {Signed(str)}");
        }

        return new DerivedAttack(toHit, damage, Describe(explicitBonus, explicitDamageBonus, hitParts, toHit, dmgParts, damage, notes));
    }

    private static string Describe(
        bool explicitBonus, bool explicitDamageBonus,
        List<string> hitParts, int toHit, List<string> dmgParts, int damage, List<string> notes)
    {
        var pieces = new List<string>();
        pieces.Add(explicitBonus && hitParts.Count == 0
            ? "to-hit: caller's bonus used as given (sheet not added)"
            : $"to-hit {Signed(toHit)} = {string.Join(", ", hitParts)}");
        pieces.Add(explicitDamageBonus && dmgParts.Count == 0
            ? "damage: caller-stated modifier (damageBonus or inside damageDice) used as given"
            : $"damage {Signed(damage)} = {string.Join(", ", dmgParts)}");
        pieces.AddRange(notes);
        return $" [Derived {string.Join("; ", pieces)}]";
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString();
}
