using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Resolves held weapon items for ruleset_action attacks and merges mechanical properties into parameters.
/// </summary>
internal static class WeaponParameterResolver
{
    private static readonly Dictionary<string, string> PropertyAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["toHitBonus"] = "bonus",
            ["attackBonus"] = "bonus",
            ["tohit"] = "bonus",
            ["damage"] = "damageDice",
            ["shots"] = "attackCount",
            ["rateOfFire"] = "attackCount",
            ["attacks"] = "attackCount",
            ["rof"] = "attackCount",
        };

    private static readonly string[] ItemIdParameterKeys =
        ["weaponItemId", "itemId", "item", "weapon"];

    public static async Task ApplyHeldWeaponDefaultsAsync(
        RulesetAction action,
        ChangeContext context,
        CancellationToken ct = default)
    {
        if (action.ActionType != RulesetActionType.Attack)
        {
            return;
        }

        var weapon = await ResolveWeaponItemAsync(action, context, ct);
        if (weapon != null)
        {
            ApplyWeaponItemProperties(action, weapon);
        }
    }

    public static async Task<Item?> ResolveWeaponItemAsync(
        RulesetAction action,
        ChangeContext context,
        CancellationToken ct = default)
    {
        if (TryGetItemId(action.Parameters, out var explicitId))
        {
            if (context.Items.TryGetValue(explicitId, out var preloaded))
            {
                return preloaded;
            }

            if (context.Session != null)
            {
                return await context.Session.LoadAsync<Item>(explicitId, ct);
            }
        }

        var heldWeapons = await GetHeldWeaponsAsync(context, action.CharacterId, ct);
        if (heldWeapons.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(action.ActionName))
        {
            var byName = heldWeapons.FirstOrDefault(w => NameMatches(w, action.ActionName));
            if (byName != null)
            {
                return byName;
            }
        }

        if (heldWeapons.Count == 1 && !action.Parameters.ContainsKey("damageDice"))
        {
            return heldWeapons[0];
        }

        return null;
    }

    /// <summary>
    /// Copies weapon Item.Properties into action.Parameters (unless already present).
    /// Property keys flow through automatically via the alias map or verbatim.
    /// Special conventions for range/AoE:
    /// - Weapon.Properties["range"] should be a SpatialDistanceBand constant (Touch/Close/Near/Far/Distant).
    ///   It flows into action.Parameters["range"], which RangeValidationHelper uses to gate single-target attacks.
    /// - For AoE spells, use action.Parameters["aoeRadius"] (instead of range) + action.Parameters["originId"] (defaults to CharacterId).
    /// - Range/AoE gating is only active if the ruleset's ICombatRuleset.EnforcesRange is true.
    /// </summary>
    public static void ApplyWeaponItemProperties(RulesetAction action, Item weapon)
    {
        if (!action.Parameters.ContainsKey("weaponItemId"))
        {
            action.Parameters["weaponItemId"] = weapon.Id;
        }

        foreach (var (rawKey, rawValue) in weapon.Properties)
        {
            var value = rawValue?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var paramKey = PropertyAliases.GetValueOrDefault(rawKey) ?? rawKey;
            if (!action.Parameters.ContainsKey(paramKey))
            {
                action.Parameters[paramKey] = value;
            }
        }

        if (string.IsNullOrWhiteSpace(action.DamageType))
        {
            var damageType = weapon.Properties.Keys
                .FirstOrDefault(k => k.Equals("damageType", StringComparison.OrdinalIgnoreCase));
            if (damageType != null && weapon.Properties.TryGetValue(damageType, out var dt))
            {
                action.DamageType = dt?.ToString();
            }
        }
    }

    public static bool TryExtractWeaponItemId(IReadOnlyDictionary<string, string> parameters, out string itemId)
    {
        itemId = string.Empty;
        return TryGetItemId(parameters, out itemId);
    }

    private static async Task<List<Item>> GetHeldWeaponsAsync(
        ChangeContext context,
        string characterId,
        CancellationToken ct = default)
    {
        var weapons = context.Items.Values
            .Where(i => string.Equals(i.HolderId, characterId, StringComparison.OrdinalIgnoreCase)
                        && i.CoreCategory == ItemCategory.Weapon)
            .ToList();

        if (weapons.Count > 0 || context.Session == null || string.IsNullOrWhiteSpace(characterId))
        {
            return weapons;
        }

        var held = await InitiativeQueryHelper.QueryItemsHeldByAsync(context.Session, characterId, ct: ct);
        return held.Where(i => i.CoreCategory == ItemCategory.Weapon).ToList();
    }

    private static bool NameMatches(Item weapon, string actionName)
    {
        if (weapon.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return weapon.Tags.Any(t => t.Equals(actionName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetItemId(IReadOnlyDictionary<string, string> parameters, out string itemId)
    {
        foreach (var key in ItemIdParameterKeys)
        {
            if (parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                itemId = value;
                return true;
            }
        }

        itemId = string.Empty;
        return false;
    }
}