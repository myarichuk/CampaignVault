using CampaignVault.Models;
using CampaignVault.Services;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface IItemManager
{
    Task<Item> UpsertItemAsync(IAsyncDocumentSession session, string campaignName, ItemUpsertRequest item);
}

internal sealed class ItemManager : IItemManager
{
    private readonly Lazy<CampaignRepository> _campaignRepository;
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public ItemManager(
        Lazy<CampaignRepository> campaignRepository,
        ILocalEmbeddingService embeddingService,
        ILogger<ItemManager> logger)
    {
        _campaignRepository = campaignRepository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<Item> UpsertItemAsync(IAsyncDocumentSession session, string campaignName, ItemUpsertRequest item)
    {
        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("Item.Id is required for upsert.");
        }

        item.Id = CanonicalId.Normalize(item.Id, CanonicalId.Items);
        item.HolderId = CanonicalId.NormalizeAlias(item.HolderId);
        var effectiveCampaignName = item.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<Item>(item.Id);
        Item result;
        if (existing != null)
        {
            existing.Name = item.Name;
            existing.Description = item.Description;
            existing.HolderId = item.HolderId;
            existing.Quantity = item.Quantity;
            existing.CurrentState = item.CurrentState;
            existing.DistinctiveFeatures = item.DistinctiveFeatures ?? existing.DistinctiveFeatures;
            existing.CoreCategory = item.CoreCategory;
            existing.Tags = item.Tags ?? existing.Tags;
            existing.Properties = item.Properties ?? existing.Properties;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (item.IsArchived.HasValue)
            {
                existing.IsArchived = item.IsArchived.Value;
            }
            existing.EquipZones = item.EquipZones ?? existing.EquipZones;
            existing.EquipLayer = item.EquipLayer ?? existing.EquipLayer;
            if (item.TwoHanded.HasValue) existing.TwoHanded = item.TwoHanded.Value;
            if (item.IsEquipped.HasValue) existing.IsEquipped = item.IsEquipped.Value;
            existing.Capacity = item.Capacity ?? existing.Capacity;
            existing.CapacityUnit = item.CapacityUnit ?? existing.CapacityUnit;
            existing.MaxCharges = item.MaxCharges ?? existing.MaxCharges;
            existing.ChargeUnit = item.ChargeUnit ?? existing.ChargeUnit;
            existing.StackGroup = item.StackGroup ?? existing.StackGroup;
            existing.RequiresEquippedTags = item.RequiresEquippedTags ?? existing.RequiresEquippedTags;
            existing.IncompatibleWithEquippedTags = item.IncompatibleWithEquippedTags ?? existing.IncompatibleWithEquippedTags;
            existing.VisualTags = item.VisualTags ?? existing.VisualTags;
            existing.AppearanceNote = item.AppearanceNote ?? existing.AppearanceNote;
            result = existing;
        }
        else
        {
            var currentDay = (await _campaignRepository.Value.GetTimeAsync(new CampaignSession(session, effectiveCampaignName))).TotalDaysElapsed;
            result = new Item
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                HolderId = item.HolderId,
                Quantity = item.Quantity,
                CurrentState = item.CurrentState,
                DistinctiveFeatures = item.DistinctiveFeatures ?? [],
                CoreCategory = item.CoreCategory,
                Tags = item.Tags ?? [],
                Properties = item.Properties ?? [],
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
                IsArchived = item.IsArchived ?? false,
                EquipZones = item.EquipZones ?? [],
                EquipLayer = item.EquipLayer,
                TwoHanded = item.TwoHanded ?? false,
                IsEquipped = item.IsEquipped ?? false,
                Capacity = item.Capacity,
                CapacityUnit = item.CapacityUnit,
                MaxCharges = item.MaxCharges,
                ChargeUnit = item.ChargeUnit,
                StackGroup = item.StackGroup,
                RequiresEquippedTags = item.RequiresEquippedTags,
                IncompatibleWithEquippedTags = item.IncompatibleWithEquippedTags,
                VisualTags = item.VisualTags,
                AppearanceNote = item.AppearanceNote,
                ItemDetails = (item.ItemDetails ?? []).Select(d => new ItemDetail
                {
                    Id = "detail-" + Guid.NewGuid(),
                    Name = d.Name,
                    Description = d.Description,
                    Status = d.Status,
                    Intent = d.Intent,
                    Origin = d.Origin,
                    TetheredToId = string.IsNullOrEmpty(d.TetheredToId) ? null : d.TetheredToId,
                    Participants = [],
                    CreatedOnDay = currentDay,
                    UpdatedOnDay = currentDay,
                    ReviewIntervalDays = d.ReviewIntervalDays,
                }).ToList(),
            };
            await session.StoreAsync(result);
        }

        if (result.IsEquipped && result.HolderId?.StartsWith("chars/", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (result.EquipZones.Count > 0 && result.EquipLayer != null)
            {
                var equipped = await session.Advanced.AsyncDocumentQuery<Item, Item_Search>()
                    .WaitForNonStaleResults(TimeSpan.FromSeconds(5))
                    .WhereEquals(x => x.HolderId, result.HolderId)
                    .Take(50)
                    .ToListAsync();

                var equippedList = equipped.Where(i => i.IsEquipped && !i.Id.Equals(result.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                var conflictResult = EquipSlotRules.FindConflicts(result, equippedList);

                if (conflictResult.HasConflicts)
                {
                    var conflictNames = string.Join(", ", conflictResult.Items.Select(c => $"{c.Name} ({c.Id})"));
                    throw new ArgumentException(
                        $"Cannot equip '{result.Name}': conflicts with {conflictNames}. " +
                        "Use the item_equip commit with replaceConflicts:true to auto-unequip conflicts.");
                }
            }
        }

        JsonSanitizer.Sanitize(result);
        foreach (var detail in result.ItemDetails)
        {
            await SemanticEnrichmentHelper.EnrichAsync(detail, _embeddingService, _logger);
        }
        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }
}
