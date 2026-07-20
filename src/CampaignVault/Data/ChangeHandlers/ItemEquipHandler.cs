using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public sealed class ItemEquipHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemEquip;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var equip = (ItemEquip)change;

        if (string.IsNullOrWhiteSpace(equip.CharacterId) || string.IsNullOrWhiteSpace(equip.ItemId))
        {
            return ChangeHandlerResult.Failure("characterId and itemId are required.");
        }

        if (!context.Characters.TryGetValue(equip.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(equip.CharacterId, ct) : null;
            if (character == null)
            {
                var hints = await context.SuggestCharacterMatchAsync(equip.CharacterId);
                var msg = $"Character {equip.CharacterId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        if (!context.Items.TryGetValue(equip.ItemId, out var item))
        {
            item = context.Session != null ? await context.Session.LoadAsync<Item>(equip.ItemId, ct) : null;
            if (item == null)
            {
                var hints = await context.SuggestItemMatchAsync(equip.ItemId);
                var msg = $"Item {equip.ItemId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewItem(item);
        }

        if (!string.Equals(item.HolderId, equip.CharacterId, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Item '{equip.ItemId}' is not carried by '{equip.CharacterId}' (currently held by '{item.HolderId}'). Transfer it there first.";
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        if (item.EquipZones.Count == 0 || item.EquipLayer == null)
        {
            var msg = $"Item '{equip.ItemId}' has no EquipZones/EquipLayer set — it is not equippable. Set these via world_build.";
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        if (item.IsEquipped)
        {
            context.RecordMessage($"Item '{equip.ItemId}' is already equipped.");
            return ChangeHandlerResult.Ok;
        }

        var equippedItems = await ItemHolderQueryHelper.GetEquippedItemsAsync(context, equip.CharacterId, item.Id, ct);
        var conflicts = EquipSlotRules.FindConflicts(item, equippedItems);

        if (conflicts.Count > 0)
        {
            if (!equip.ReplaceConflicts)
            {
                var names = string.Join(", ", conflicts.Select(c => $"{c.Name} ({c.Id})"));
                var msg = $"Equipping '{item.Name}' conflicts with already-equipped item(s): {names}. " +
                          "Set replaceConflicts:true to auto-unequip them, or unequip manually first.";
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }

            foreach (var conflict in conflicts)
            {
                conflict.IsEquipped = false;
                conflict.LastUpdated = DateTime.UtcNow;
                context.RegisterNewItem(conflict);
            }
            context.RecordMessage($"Unequipped {string.Join(", ", conflicts.Select(c => c.Name))} to make room for '{item.Name}'.");
        }

        item.IsEquipped = true;
        item.LastUpdated = DateTime.UtcNow;
        context.RecordMessage($"Equipped '{item.Name}' on {equip.CharacterId}.");

        await ArmorParameterResolver.ApplyAsync(character, context, ct);
        context.RecordMessage($"{character.Name}'s ArmorClass and WarmthRating recomputed.");

        return ChangeHandlerResult.Ok;
    }
}

public sealed class ItemUnequipHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemUnequip;

    public async Task<ChangeHandlerResult> ApplyAsync(
        WorldChange change,
        ChangeContext context,
        CancellationToken ct = default)
    {
        var unequip = (ItemUnequip)change;

        if (string.IsNullOrWhiteSpace(unequip.CharacterId) || string.IsNullOrWhiteSpace(unequip.ItemId))
        {
            return ChangeHandlerResult.Failure("characterId and itemId are required.");
        }

        if (!context.Items.TryGetValue(unequip.ItemId, out var item))
        {
            item = context.Session != null ? await context.Session.LoadAsync<Item>(unequip.ItemId, ct) : null;
            if (item == null)
            {
                var hints = await context.SuggestItemMatchAsync(unequip.ItemId);
                var msg = $"Item {unequip.ItemId} not found.";
                if (hints != null) msg += $" Did you mean: {hints}?";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewItem(item);
        }

        if (!string.Equals(item.HolderId, unequip.CharacterId, StringComparison.OrdinalIgnoreCase))
        {
            var msg = $"Item '{unequip.ItemId}' is not carried by '{unequip.CharacterId}'.";
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        if (!item.IsEquipped)
        {
            context.RecordMessage($"Item '{unequip.ItemId}' is already unequipped.");
            return ChangeHandlerResult.Ok;
        }

        if (!context.Characters.TryGetValue(unequip.CharacterId, out var character))
        {
            character = context.Session != null ? await context.Session.LoadAsync<Character>(unequip.CharacterId, ct) : null;
            if (character == null)
            {
                var msg = $"Character {unequip.CharacterId} not found.";
                context.RecordMessage($"WARNING: {msg}");
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewCharacter(character);
        }

        item.IsEquipped = false;
        item.LastUpdated = DateTime.UtcNow;
        context.RecordMessage($"Unequipped '{item.Name}' from {unequip.CharacterId}.");

        await ArmorParameterResolver.ApplyAsync(character, context, ct);
        context.RecordMessage($"{character.Name}'s ArmorClass and WarmthRating recomputed.");

        return ChangeHandlerResult.Ok;
    }
}
