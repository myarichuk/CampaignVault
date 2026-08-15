using CampaignVault.Models;
using CampaignVault.Services;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface IEntityManager
{
    Task<Faction> UpsertFactionAsync(IAsyncDocumentSession session, string campaignName, FactionUpsertRequest faction);
    Task<Quest> UpsertQuestAsync(IAsyncDocumentSession session, string campaignName, QuestUpsertRequest quest, int currentDay);
    Task<PlotThread> UpsertPlotThreadAsync(IAsyncDocumentSession session, string campaignName, PlotThreadUpsertRequest thread, int currentDay);
    Task<WorldEvent> UpsertWorldEventAsync(IAsyncDocumentSession session, string campaignName, WorldEventUpsertRequest eventRequest, int currentDay);
}

internal sealed class EntityManager : IEntityManager
{
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public EntityManager(
        ILocalEmbeddingService embeddingService,
        ILogger<EntityManager> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<Faction> UpsertFactionAsync(IAsyncDocumentSession session, string campaignName, FactionUpsertRequest faction)
    {
        if (string.IsNullOrWhiteSpace(faction.Id))
        {
            throw new ArgumentException("Faction.Id is required for upsert.");
        }

        faction.Id = CanonicalId.Normalize(faction.Id, CanonicalId.Factions);

        var effectiveCampaignName = faction.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<Faction>(faction.Id);
        Faction result;
        if (existing != null)
        {
            existing.Name = faction.Name;
            existing.Description = faction.Description;
            existing.FactionType = faction.FactionType;
            existing.ControllingTerritory = faction.ControllingTerritory;
            existing.TerritoryLocationIds = faction.TerritoryLocationIds ?? existing.TerritoryLocationIds;
            existing.KnownLeaderIds = faction.KnownLeaderIds ?? existing.KnownLeaderIds;
            if (faction.InfluenceLevel.HasValue)
            {
                existing.InfluenceLevel = Math.Clamp(faction.InfluenceLevel.Value, 0, 100);
            }
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            if (faction.IsArchived.HasValue)
            {
                existing.IsArchived = faction.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Faction
            {
                Id = faction.Id,
                Name = faction.Name,
                Description = faction.Description,
                FactionType = faction.FactionType,
                ControllingTerritory = faction.ControllingTerritory,
                TerritoryLocationIds = faction.TerritoryLocationIds ?? [],
                KnownLeaderIds = faction.KnownLeaderIds ?? [],
                InfluenceLevel = Math.Clamp(faction.InfluenceLevel ?? 50, 0, 100),
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                IsArchived = faction.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }

    public async Task<Quest> UpsertQuestAsync(IAsyncDocumentSession session, string campaignName, QuestUpsertRequest quest, int currentDay)
    {
        if (string.IsNullOrWhiteSpace(quest.Id))
        {
            throw new ArgumentException("Quest.Id is required for upsert.");
        }

        quest.Id = CanonicalId.Normalize(quest.Id, CanonicalId.Quests);

        var effectiveCampaignName = quest.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<Quest>(quest.Id);
        Quest result;
        if (existing != null)
        {
            existing.Title = quest.Title;
            existing.GiverId = quest.GiverId;
            existing.Objectives = quest.Objectives ?? existing.Objectives;
            existing.Category = quest.Category;
            existing.Urgency = quest.Urgency;
            existing.RelatedLocationIds = quest.RelatedLocationIds ?? existing.RelatedLocationIds;
            existing.RelatedFactionIds = quest.RelatedFactionIds ?? existing.RelatedFactionIds;
            existing.DmNotes = quest.DmNotes;
            existing.DeadlineDay = quest.DeadlineDay;
            existing.LastUpdated = DateTime.UtcNow;
            existing.LastUpdatedDay = currentDay;
            existing.CampaignName = effectiveCampaignName;
            if (quest.IsArchived.HasValue)
            {
                existing.IsArchived = quest.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new Quest
            {
                Id = quest.Id,
                Title = quest.Title,
                GiverId = quest.GiverId,
                Objectives = quest.Objectives ?? [],
                Category = quest.Category,
                Urgency = quest.Urgency,
                RelatedLocationIds = quest.RelatedLocationIds ?? [],
                RelatedFactionIds = quest.RelatedFactionIds ?? [],
                DmNotes = quest.DmNotes,
                DeadlineDay = quest.DeadlineDay,
                CampaignName = effectiveCampaignName,
                LastUpdated = DateTime.UtcNow,
                LastUpdatedDay = currentDay,
                IsArchived = quest.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }

    public async Task<PlotThread> UpsertPlotThreadAsync(IAsyncDocumentSession session, string campaignName, PlotThreadUpsertRequest thread, int currentDay)
    {
        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            throw new ArgumentException("PlotThread.Id is required for upsert.");
        }

        thread.Id = CanonicalId.Normalize(thread.Id, CanonicalId.PlotThreads);

        var effectiveCampaignName = thread.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<PlotThread>(thread.Id);
        PlotThread result;
        if (existing != null)
        {
            existing.Title = thread.Title;
            existing.Summary = thread.Summary;
            existing.State = thread.State;
            existing.TensionLevel = thread.TensionLevel;
            existing.Clues = thread.Clues ?? existing.Clues;
            existing.InvolvedEntityIds = thread.InvolvedEntityIds ?? existing.InvolvedEntityIds;
            existing.ResolutionCondition = thread.ResolutionCondition;
            existing.ForeshadowingHooks = thread.ForeshadowingHooks ?? existing.ForeshadowingHooks;
            existing.DmNotes = thread.DmNotes;
            existing.DeadlineDay = thread.DeadlineDay;
            existing.IsPlayerVisible = thread.IsPlayerVisible;
            existing.CampaignName = effectiveCampaignName;
            existing.LastUpdatedDay = currentDay;
            if (thread.IsArchived.HasValue)
            {
                existing.IsArchived = thread.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new PlotThread
            {
                Id = thread.Id,
                Title = thread.Title,
                Summary = thread.Summary,
                State = thread.State,
                TensionLevel = thread.TensionLevel,
                Clues = thread.Clues ?? [],
                InvolvedEntityIds = thread.InvolvedEntityIds ?? [],
                ResolutionCondition = thread.ResolutionCondition,
                ForeshadowingHooks = thread.ForeshadowingHooks ?? [],
                DmNotes = thread.DmNotes,
                DeadlineDay = thread.DeadlineDay,
                IsPlayerVisible = thread.IsPlayerVisible,
                CampaignName = effectiveCampaignName,
                DayCreated = currentDay,
                LastUpdatedDay = currentDay,
                IsArchived = thread.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }

    public async Task<WorldEvent> UpsertWorldEventAsync(IAsyncDocumentSession session, string campaignName, WorldEventUpsertRequest eventRequest, int currentDay)
    {
        if (string.IsNullOrWhiteSpace(eventRequest.Id))
        {
            throw new ArgumentException("WorldEvent.Id is required for upsert.");
        }

        eventRequest.Id = CanonicalId.Normalize(eventRequest.Id, CanonicalId.WorldEvents);

        var effectiveCampaignName = eventRequest.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<WorldEvent>(eventRequest.Id);
        WorldEvent result;
        if (existing != null)
        {
            existing.Title = eventRequest.Title;
            existing.Description = eventRequest.Description;
            existing.ActorId = eventRequest.ActorId;
            existing.InvolvedEntityIds = eventRequest.InvolvedEntityIds ?? existing.InvolvedEntityIds;
            existing.TriggerType = eventRequest.TriggerType;
            existing.IntervalDays = eventRequest.IntervalDays;
            existing.TargetDay = eventRequest.TargetDay;
            existing.Condition = eventRequest.Condition ?? existing.Condition;
            existing.Effects = eventRequest.Effects ?? existing.Effects;
            existing.Status = eventRequest.Status;
            existing.IsPlayerVisible = eventRequest.IsPlayerVisible;
            existing.DmNotes = eventRequest.DmNotes;
            existing.CampaignName = effectiveCampaignName;
            existing.LastUpdatedDay = currentDay;
            if (eventRequest.IsArchived.HasValue)
            {
                existing.IsArchived = eventRequest.IsArchived.Value;
            }
            result = existing;
        }
        else
        {
            result = new WorldEvent
            {
                Id = eventRequest.Id,
                Title = eventRequest.Title,
                Description = eventRequest.Description,
                ActorId = eventRequest.ActorId,
                InvolvedEntityIds = eventRequest.InvolvedEntityIds ?? [],
                TriggerType = eventRequest.TriggerType,
                IntervalDays = eventRequest.IntervalDays,
                TargetDay = eventRequest.TargetDay,
                Condition = eventRequest.Condition,
                Effects = eventRequest.Effects ?? [],
                Status = eventRequest.Status,
                IsPlayerVisible = eventRequest.IsPlayerVisible,
                DmNotes = eventRequest.DmNotes,
                CampaignName = effectiveCampaignName,
                DayCreated = currentDay,
                LastUpdatedDay = currentDay,
                IsArchived = eventRequest.IsArchived ?? false,
            };
            await session.StoreAsync(result);
        }

        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }
}
