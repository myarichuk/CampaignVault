using System.Globalization;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Ammunition and fire modes for ranged ruleset_action attacks (SDK 0.10.0). A weapon opts in with the property
/// <c>ammoType</c> (and optionally <c>ammoPerShot</c>, <c>fireModes</c>); weapons without <c>ammoType</c> (melee, thrown,
/// spells) are untouched. Ammo is an ordinary held item: a Consumable whose <c>ammoFor</c> (or <c>ammoType</c>) names the
/// weapon's key or ammo type, with the rounds kept as item charges (or the stack's quantity).
/// </summary>
internal static class AmmoResolver
{
    public sealed record AmmoPlan(Item Ammo, int Rounds, int Spend, string Note);

    public static string Normalize(string? s) =>
        string.Join("_", (s ?? "").ToLowerInvariant().Split(
            [' ', ',', '-', '/', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Applies the weapon's fire mode and finds the ammo. Null plan and null failure: this weapon uses no ammo. On success
    /// the action's <c>attackCount</c> already reflects the fire mode and what the ammo can cover.
    /// </summary>
    public static async Task<(ChangeHandlerResult? Failure, AmmoPlan? Plan)> PrepareAsync(
        RulesetAction action, ChangeContext ctx, bool callerSetCount, CancellationToken ct)
    {
        if (action.ActionType != RulesetActionType.Attack ||
            !action.Parameters.TryGetValue("weaponItemId", out var weaponId) ||
            await LoadAsync(ctx, weaponId, ct) is not { } weapon ||
            Prop(weapon, "ammoType") is not { Length: > 0 } ammoType)
            return (null, null);

        if (ApplyFireMode(action, weapon, callerSetCount) is { } modeError)
            return (ChangeHandlerResult.Failure(modeError), null);

        var perShot = int.TryParse(Prop(weapon, "ammoPerShot"), out var p) && p > 0 ? p : 1;
        var targets = action.TargetIds.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var wanted = AttackTargetHelper.ResolveAttackCount(action, targets);

        var keys = new HashSet<string> { Normalize(ammoType), Normalize(weapon.DefinitionName), Normalize(weapon.Name) };
        keys.Remove("");
        Item? ammo = null;
        if (action.Parameters.TryGetValue("ammoItemId", out var explicitId) && !string.IsNullOrWhiteSpace(explicitId))
        {
            ammo = await LoadAsync(ctx, explicitId, ct);
            if (ammo is null || !string.Equals(ammo.HolderId, action.CharacterId, StringComparison.OrdinalIgnoreCase))
                return (ChangeHandlerResult.Failure($"[NoAmmo] ammoItemId '{explicitId}' is not carried by {action.CharacterId}."), null);
        }
        else
        {
            var held = await ItemHolderQueryHelper.GetHeldItemsAsync(ctx, action.CharacterId, ct);
            ammo = held.Where(i => Fits(i, weapon.Id, keys)).OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault(i => Rounds(i) > 0)
                   ?? held.FirstOrDefault(i => Fits(i, weapon.Id, keys));
        }

        var rounds = ammo is null ? 0 : Rounds(ammo);
        if (ammo is null || rounds < perShot)
        {
            return (ChangeHandlerResult.Failure(
                $"[NoAmmo] {action.CharacterId} has no {ammoType} ammunition for {weapon.Name} " +
                $"(needs {perShot} per shot; ammo lists the weapon in ammoFor)."), null);
        }

        var affordable = rounds / perShot;
        var note = "";
        if (affordable < wanted)
        {
            action.Parameters["attackCount"] = affordable.ToString(CultureInfo.InvariantCulture);
            wanted = affordable;
            note = $" Only {rounds} {ammo.ChargeUnit ?? "rounds"} left: fired {affordable} of the requested shots.";
        }

        // A trick round (fire arrow) adds its own damage on every hit; the resolver reads these two parameters.
        if (Prop(ammo, "damage") is { Length: > 0 } rider && !action.Parameters.ContainsKey("riderDice"))
        {
            action.Parameters["riderDice"] = rider;
            if (Prop(ammo, "damageType") is { Length: > 0 } riderType)
                action.Parameters["riderType"] = riderType;
        }

        return (null, new AmmoPlan(ammo, rounds, wanted * perShot, note));
    }

    /// <summary>Takes the rounds out of the ammo item and returns a one-line report.</summary>
    public static string Spend(AmmoPlan plan)
    {
        var ammo = plan.Ammo;
        // A bundle that only declares `uses` becomes a charged item the first time it is fired (as item_use would).
        if (ammo.MaxCharges is null && DeclaredUses(ammo) is { } declared)
        {
            ammo.MaxCharges = declared;
            ammo.CurrentCharges ??= declared;
            ammo.ChargeUnit ??= Prop(ammo, "chargeUnit");
        }

        var unit = ammo.ChargeUnit ?? "rounds";
        int left;
        if (ammo.MaxCharges is not null)
        {
            left = plan.Rounds - plan.Spend;
            ammo.CurrentCharges = left;
        }
        else
        {
            ammo.Quantity = left = plan.Rounds - plan.Spend;
        }

        ammo.LastUpdated = DateTime.UtcNow;
        return $"Ammo: {ammo.Name} {plan.Rounds} → {left} {unit}.{plan.Note}";
    }

    /// <summary>
    /// Rounds left: the item's charges; else, for a definition that declares <c>uses</c> (a 20-bolt bundle) but was never
    /// given charges, that many; else the stack's quantity.
    /// </summary>
    public static int Rounds(Item item)
    {
        if (item.MaxCharges is { } max)
            return Math.Max(0, item.CurrentCharges ?? max);
        return DeclaredUses(item) is { } uses ? Math.Max(0, item.CurrentCharges ?? uses) : Math.Max(0, item.Quantity);
    }

    private static int? DeclaredUses(Item item) =>
        int.TryParse(Prop(item, "uses"), out var n) && n > 0 ? n : null;

    private static bool Fits(Item item, string weaponId, HashSet<string> keys)
    {
        // The weapon's own ammoType says what it takes, not that it is ammo; only consumables (or things that declare
        // ammoFor) are candidates.
        if (string.Equals(item.Id, weaponId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.CoreCategory != ItemCategories.Consumable && Prop(item, "ammoFor") is null)
            return false;
        foreach (var name in new[] { "ammoFor", "ammoType" })
        {
            if (Prop(item, name) is { } v && v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(token => keys.Contains(Normalize(token))))
                return true;
        }

        return false;
    }

    /// <summary>
    /// "single:1, burst:3, auto:10": <c>mode</c>/<c>fireMode</c> names one and sets <c>attackCount</c>, unless the caller
    /// already set the count itself. Returns an error text when the mode is not one of the weapon's.
    /// </summary>
    private static string? ApplyFireMode(RulesetAction action, Item weapon, bool callerSetCount)
    {
        var requested = action.Parameters.GetValueOrDefault("mode") ?? action.Parameters.GetValueOrDefault("fireMode");
        if (string.IsNullOrWhiteSpace(requested))
            return null;

        var modes = ParseModes(Prop(weapon, "fireModes"));
        if (modes.Count == 0)
            return $"{weapon.Name} has no fire modes (fireModes), so mode '{requested}' cannot be used.";
        if (!modes.TryGetValue(requested.Trim(), out var count))
            return $"{weapon.Name} has no fire mode '{requested}'. Modes: {string.Join(", ", modes.Select(m => $"{m.Key}:{m.Value}"))}.";

        if (!callerSetCount)
            action.Parameters["attackCount"] = count.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    internal static Dictionary<string, int> ParseModes(string? raw)
    {
        var modes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split([':', '='], 2, StringSplitOptions.TrimEntries);
            if (pieces.Length == 2 && pieces[0].Length > 0 && int.TryParse(pieces[1], out var n) && n > 0)
                modes[pieces[0]] = n;
        }

        return modes;
    }

    private static string? Prop(Item item, string key)
    {
        foreach (var (k, v) in item.Properties)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && v?.ToString() is { Length: > 0 } s)
                return s;
        }

        return null;
    }

    private static async Task<Item?> LoadAsync(ChangeContext ctx, string id, CancellationToken ct)
    {
        if (ctx.Items.TryGetValue(id, out var known))
            return known;
        if (ctx.Session is null)
            return null;
        var item = await ctx.Session.LoadAsync<Item>(id, ct);
        if (item is not null)
            ctx.RegisterNewItem(item);
        return item;
    }
}
