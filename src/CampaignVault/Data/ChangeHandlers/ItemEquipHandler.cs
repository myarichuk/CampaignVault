using System.Text;
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

        // Tag-based prerequisite/incompatibility checks are declared design statements, independent of
        // zone/layer/StackGroup slot capacity. They always hard-fail — never auto-resolved by
        // replaceConflicts, unlike slot conflicts below.
        var tagCheck = EquipSlotRules.FindTagIncompatibilities(item, equippedItems);
        if (tagCheck.HasIssues)
        {
            var msg = BuildTagIssueMessage(item, tagCheck);
            context.RecordFailure();
            return ChangeHandlerResult.Failure(msg);
        }

        var conflictResult = EquipSlotRules.FindConflicts(item, equippedItems);

        if (conflictResult.HasConflicts)
        {
            if (!equip.ReplaceConflicts)
            {
                var msg = BuildConflictMessage(item, conflictResult);
                var reorderNudge = BuildBatchReorderNudge(context, conflictResult);
                if (reorderNudge != null)
                {
                    msg += "\n" + reorderNudge;
                }
                context.RecordFailure();
                return ChangeHandlerResult.Failure(msg);
            }

            foreach (var conflict in conflictResult.Items)
            {
                conflict.IsEquipped = false;
                conflict.LastUpdated = DateTime.UtcNow;
                context.RegisterNewItem(conflict);
            }
            context.RecordMessage(BuildReplaceMessage(item, conflictResult));
        }

        item.IsEquipped = true;
        item.LastUpdated = DateTime.UtcNow;
        context.RecordMessage($"Equipped '{item.Name}' on {equip.CharacterId}.");

        await ArmorParameterResolver.ApplyAsync(character, context, ct);
        context.RecordMessage($"{character.Name}'s ArmorClass and WarmthRating recomputed.");

        return ChangeHandlerResult.Ok;
    }

    private static string BuildTagIssueMessage(Item item, EquipSlotRules.TagCheckResult tagCheck)
    {
        var sb = new StringBuilder();
        sb.Append($"ENGINE WARNING: Cannot equip '{item.Name}' ({item.Id})");

        if (tagCheck.MissingPrerequisiteTags.Count > 0)
        {
            sb.Append(tagCheck.MissingPrerequisiteTags.Count == 1 ? " — missing prerequisite:" : " — missing prerequisites:");
            foreach (var tag in tagCheck.MissingPrerequisiteTags)
            {
                sb.Append($"\n  requires an equipped item tagged '{tag}', none found.");
            }
        }

        if (tagCheck.Incompatibilities.Count > 0)
        {
            sb.Append(tagCheck.MissingPrerequisiteTags.Count > 0
                ? "\nAlso incompatible with currently-equipped item(s):"
                : " — incompatible with currently-equipped item(s):");
            foreach (var incompat in tagCheck.Incompatibilities)
            {
                sb.Append($"\n  '{incompat.ConflictingItem.Name}' ({incompat.ConflictingItem.Id}) carries tag '{incompat.Tag}', which conflicts with this item's IncompatibleWithEquippedTags.");
            }
        }

        if (tagCheck.MissingPrerequisiteTags.Count > 0)
        {
            sb.Append(" Equip the missing prerequisite item(s) first, or remove RequiresEquippedTags if this piece can stand alone.");
        }
        if (tagCheck.Incompatibilities.Count > 0)
        {
            sb.Append(" Unequip the conflicting item(s) first, or remove the incompatibility tag if this combination is intended.");
        }

        return sb.ToString();
    }

    private static string BuildConflictMessage(Item item, EquipSlotRules.ConflictResult result)
    {
        var noun = result.Zones.Count == 1 ? "slot conflict" : "slot conflicts";
        var sb = new StringBuilder();
        sb.Append($"ENGINE WARNING: Cannot equip '{item.Name}' ({item.Id}) — {result.Zones.Count} {noun}:");
        foreach (var zc in result.Zones)
        {
            var stackDesc = zc.StackGroup == null ? "no StackGroup" : $"StackGroup '{zc.StackGroup}'";
            var occupants = string.Join(", ", zc.ToFree.Select(o => $"'{o.Name}' ({o.Id})"));
            sb.Append($"\n  - {zc.Zone}/{zc.Layer} (capacity {zc.Occupied}/{zc.Capacity}, {stackDesc}): occupied by {occupants}");
        }
        sb.Append($"\nSet replaceConflicts:true to auto-unequip the listed item(s), or unequip manually first. " +
                   $"If '{item.Name}' is a modular add-on meant to coexist with what's already worn there, set a StackGroup on it via world_build so it stops competing for this slot.");
        return sb.ToString();
    }

    private static string BuildReplaceMessage(Item item, EquipSlotRules.ConflictResult result)
    {
        var freed = result.Zones.SelectMany(zc => zc.ToFree.Select(o => $"'{o.Name}' ({zc.Zone}/{zc.Layer})"));
        var itemZones = string.Join("+", result.Zones.Select(z => $"{z.Zone}/{z.Layer}").Distinct());
        return $"Unequipped {string.Join(", ", freed)} to make room for '{item.Name}' ({itemZones}).";
    }

    /// <summary>
    /// If this batch also unequips one of the conflicting occupants later in the same commit array,
    /// nudge the caller to reorder rather than just hard-failing on a generic conflict — the batch is
    /// already atomic (see CampaignToolBase.ExecuteAsync), the only real gap is that item_equip only
    /// sees conflicts freed earlier in the same batch, not later.
    /// </summary>
    private static string? BuildBatchReorderNudge(ChangeContext context, EquipSlotRules.ConflictResult conflictResult)
    {
        if (context.Batch == null)
        {
            return null;
        }

        var reorderNames = new List<string>();
        for (var i = context.BatchIndex + 1; i < context.Batch.Count; i++)
        {
            if (context.Batch[i] is not ItemUnequip laterUnequip)
            {
                continue;
            }

            var match = conflictResult.Items.FirstOrDefault(c => c.Id.Equals(laterUnequip.ItemId, StringComparison.OrdinalIgnoreCase));
            if (match != null && !reorderNames.Contains(match.Name))
            {
                reorderNames.Add(match.Name);
            }
        }

        if (reorderNames.Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", reorderNames.Select(n => $"'{n}'"));
        return $"NARRATIVE PROMPT: {names} conflicts here, but this batch also unequips it later — " +
               "reorder so item_unequip precedes item_equip in the array, or set replaceConflicts:true.";
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
