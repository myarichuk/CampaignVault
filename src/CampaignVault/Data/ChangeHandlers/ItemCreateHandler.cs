using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

/// <summary>
/// Handles <c>item_create</c>: a new item introduced mid-play (a gift, loot, a dropped note), held by a
/// character, location or container that already exists or was created earlier in the same batch.
/// An existing <c>itemId</c> fails loudly: the engine never silently merges a second "coin purse" onto
/// the first, since that is how an item ends up listed on the wrong person.
/// </summary>
public sealed class ItemCreateHandler(ItemDefinitionProvider itemDefinitions, CampaignDocumentKeys keys) : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var ic = (ItemCreate)change;

        if (string.IsNullOrWhiteSpace(ic.ItemId)) return ChangeHandlerResult.Failure("itemId is required.");
        if (string.IsNullOrWhiteSpace(ic.Name)) return ChangeHandlerResult.Failure($"item_create {ic.ItemId}: name is required.");
        if (string.IsNullOrWhiteSpace(ic.HolderId))
            return ChangeHandlerResult.Failure($"item_create {ic.ItemId}: holderId is required (a character, location or container item).");

        try
        {
            ic.ItemId = CanonicalId.Normalize(ic.ItemId, CanonicalId.Items);
            ic.HolderId = CanonicalId.NormalizeAlias(ic.HolderId);
        }
        catch (ArgumentException ex)
        {
            return ChangeHandlerResult.Failure(ex.Message);
        }

        if (ctx.Items.ContainsKey(ic.ItemId) || await ctx.Session.LoadAsync<Item>(ic.ItemId, ct) != null)
        {
            return ChangeHandlerResult.Failure(
                $"Item {ic.ItemId} already exists. To hand it over use {{\"$type\":\"item\",\"itemId\":\"{ic.ItemId}\",\"toHolderId\":\"{ic.HolderId}\"}}; to change it use item_update.");
        }

        var holderExists = ctx.Characters.ContainsKey(ic.HolderId)
            || ctx.Locations.ContainsKey(ic.HolderId)
            || ctx.Items.ContainsKey(ic.HolderId);
        Item? container = ctx.Items.GetValueOrDefault(ic.HolderId);
        if (!holderExists)
        {
            if (ic.HolderId.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
            {
                container = await ctx.Session.LoadAsync<Item>(ic.HolderId, ct);
                holderExists = container != null;
            }
            else if (ic.HolderId.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
            {
                holderExists = await ctx.Session.LoadAsync<Character>(ic.HolderId, ct) != null;
            }
            else
            {
                holderExists = await ctx.Session.LoadAsync<Location>(ic.HolderId, ct) != null;
            }
        }

        if (!holderExists)
        {
            return ChangeHandlerResult.Failure(
                $"item_create {ic.ItemId}: holder {ic.HolderId} does not exist. Create it first (character_create / location_update earlier in this batch) or pick an existing holder.");
        }

        ItemDefinition? definition = null;
        if (!string.IsNullOrWhiteSpace(ic.DefinitionName) && !string.IsNullOrEmpty(ctx.CampaignName))
        {
            var system = await CharacterHandlerHelpers.ResolveActiveSystemAsync(ctx, keys, ct);
            if (!itemDefinitions.TryGet(system, ic.DefinitionName, out definition))
            {
                ctx.RecordMessage($"WARNING: item_create {ic.ItemId}: no {system} item template named '{ic.DefinitionName}'; created from the explicit fields only (lookup kind=items to find template names).");
            }
        }

        var item = new Item
        {
            Id = ic.ItemId,
            Name = ic.Name,
            Description = ic.Description ?? definition?.Description ?? string.Empty,
            HolderId = ic.HolderId,
            Quantity = ic.Quantity is > 0 ? ic.Quantity.Value : 1,
            CurrentState = ic.CurrentState,
            DistinctiveFeatures = ic.DistinctiveFeatures ?? [],
            CoreCategory = ic.CoreCategory ?? definition?.Category ?? ItemCategories.Other,
            Tags = ic.Tags ?? definition?.Tags ?? [],
            Properties = ic.Properties ?? definition?.Properties ?? [],
            DefinitionName = definition?.Name ?? ic.DefinitionName,
            EquipZones = ic.EquipZones ?? definition?.EquipZones ?? [],
            EquipLayer = ic.EquipLayer ?? definition?.EquipLayer,
            TwoHanded = ic.TwoHanded ?? definition?.TwoHanded ?? false,
            StackGroup = definition?.StackGroup,
            RequiresEquippedTags = definition?.RequiresEquippedTags,
            IncompatibleWithEquippedTags = definition?.IncompatibleWithEquippedTags,
            Hidden = ic.Hidden ?? false,
            DiscoverDc = ic.DiscoverDc,
            Hazard = ic.Hazard,
            MaxCharges = ic.MaxCharges,
            CurrentCharges = ic.MaxCharges,
            ChargeUnit = ic.ChargeUnit,
            Capacity = ic.Capacity,
            CapacityUnit = ic.CapacityUnit,
            VisualTags = ic.VisualTags,
            AppearanceNote = ic.AppearanceNote,
            LastUpdated = DateTime.UtcNow,
            CampaignName = ctx.CampaignName,
        };

        if (container != null)
        {
            var nestingError = await ContainerResolver.ValidateNestingAsync(ctx.Session, item, container, ct);
            if (nestingError != null)
            {
                return ChangeHandlerResult.Failure(nestingError);
            }
        }

        await ctx.Session.StoreAsync(item, ct);
        ctx.RegisterNewItem(item);

        var holderName = ctx.Characters.GetValueOrDefault(ic.HolderId)?.Name
            ?? ctx.Locations.GetValueOrDefault(ic.HolderId)?.Name
            ?? container?.Name
            ?? ic.HolderId;
        ctx.RecordMessage($"Created item {item.Name} ({item.Id}) held by {holderName}.");
        return ChangeHandlerResult.Ok;
    }
}
