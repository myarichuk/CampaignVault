using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Services;

namespace CampaignVault.Data.ChangeHandlers;

public class ItemUpdateHandler(ILocalEmbeddingService embeddingService) : IWorldChangeHandler
{
    private const double ItemDetailSemanticMatchThreshold = 0.86;

    public bool ShouldHandle(WorldChange change) => change is ItemUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, IChangeContext context, CancellationToken ct = default)
    {
        var ctx = (ChangeContext)context;
        var iu = (ItemUpdate)change;
        if (string.IsNullOrWhiteSpace(iu.ItemId)) return ChangeHandlerResult.Failure("itemId is required.");

        var item = await ctx.Session.LoadAsync<Item>(iu.ItemId, ct);
        if (item == null) return ChangeHandlerResult.Failure($"Item '{iu.ItemId}' not found. Cannot update.");

        var stateBefore = item.CurrentState;
        var tagsBefore = new HashSet<string>(item.Tags);
        var featuresBefore = new HashSet<string>(item.DistinctiveFeatures);

        if (iu.NewState != null) item.CurrentState = iu.NewState;
        if (iu.CoreCategory != null) item.CoreCategory = iu.CoreCategory;
        if (iu.Hidden != null) item.Hidden = iu.Hidden.Value;
        if (iu.DiscoverDc != null) item.DiscoverDc = iu.DiscoverDc;
        if (iu.Hazard != null) item.Hazard = iu.Hazard;

        if (iu.TagsToAdd != null)
        {
            item.Tags = item.Tags.Union(iu.TagsToAdd).Distinct().ToList();
        }
        if (iu.TagsToRemove != null)
        {
            item.Tags.RemoveAll(t => iu.TagsToRemove.Contains(t));
            foreach (var removed in iu.TagsToRemove) item.TagProvenance.Remove(removed);
        }

        if (iu.FeaturesToAdd != null)
        {
            item.DistinctiveFeatures = item.DistinctiveFeatures.Union(iu.FeaturesToAdd).Distinct().ToList();
        }
        if (iu.FeaturesToRemove != null)
        {
            item.DistinctiveFeatures.RemoveAll(f => iu.FeaturesToRemove.Contains(f));
            foreach (var removed in iu.FeaturesToRemove) item.TagProvenance.Remove(removed);
        }

        if (iu.PropertiesToUpsert != null)
        {
            foreach (var kv in iu.PropertiesToUpsert) item.Properties[kv.Key] = kv.Value;
        }
        if (iu.PropertiesToRemove != null)
        {
            foreach (var k in iu.PropertiesToRemove) item.Properties.Remove(k);
        }

        if (iu.AmbientPersistenceNote != null || iu.AmbientExpiresAtDay.HasValue)
        {
            item.Persistence ??= new AmbientPersistence();
            if (iu.AmbientPersistenceNote != null)
            {
                item.Persistence.Note = iu.AmbientPersistenceNote;
            }
            if (iu.AmbientExpiresAtDay.HasValue)
            {
                item.Persistence.ExpiresAtDay = iu.AmbientExpiresAtDay.Value;
                // A fresh expiry means any previously-surfaced nag should fire again at the new day.
                item.Persistence.PressureSurfaced = false;
            }
        }

        if (iu.UpsertItemDetail != null)
        {
            var detailResult = await UpsertItemDetailAsync(item, iu.UpsertItemDetail, ctx, ct);
            if (detailResult != null) return detailResult.Value;
        }

        if (!string.IsNullOrWhiteSpace(iu.RetireItemDetailId))
        {
            var retireResult = await RetireItemDetailAsync(item, iu.RetireItemDetailId, ctx, ct);
            if (retireResult != null) return retireResult.Value;
        }

        // Item condition/appearance (singed cuffs, cracked hilt) is otherwise only recoverable from
        // conversation memory. Auto-log a low-weight history entry, mirroring Character/LocationUpdateHandler.
        var stateChanged = item.CurrentState != stateBefore
            || !tagsBefore.SetEquals(item.Tags)
            || !featuresBefore.SetEquals(item.DistinctiveFeatures);

        if (stateChanged)
        {
            var eventId = "events/" + Guid.NewGuid();
            await ctx.LogEventAsync(new Event
            {
                Id = eventId,
                Summary = $"{item.Name}'s state changed: {item.CurrentState ?? "(no override)"}; tags: [{string.Join(", ", item.Tags)}]",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                RelatedEntityId = iu.ItemId,
                Involved = [iu.ItemId],
                LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
                DayLogged = (await ctx.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = ctx.CampaignName,
            });

            if (item.CurrentState != stateBefore)
            {
                if (stateBefore != null) item.TagProvenance.Remove(stateBefore);
                if (item.CurrentState != null) item.TagProvenance[item.CurrentState] = [eventId];
            }
            foreach (var addedTag in item.Tags.Except(tagsBefore))
            {
                item.TagProvenance[addedTag] = [eventId];
            }
            foreach (var addedFeature in item.DistinctiveFeatures.Except(featuresBefore))
            {
                item.TagProvenance[addedFeature] = [eventId];
            }
        }

        if (item.IsEquipped && (iu.PropertiesToUpsert != null || iu.PropertiesToRemove != null)
            && !string.IsNullOrEmpty(item.HolderId))
        {
            if (!ctx.Characters.TryGetValue(item.HolderId, out var wearer))
            {
                wearer = await ctx.Session.LoadAsync<Character>(item.HolderId, ct);
                if (wearer != null) ctx.RegisterNewCharacter(wearer);
            }

            if (wearer != null)
            {
                await ArmorParameterResolver.ApplyAsync(wearer, ctx, ct);
                ctx.RecordMessage(DerivedStatsMessage.Build(wearer));
            }
        }

        return ChangeHandlerResult.Ok;
    }

    private async Task<ChangeHandlerResult?> UpsertItemDetailAsync(Item item, ItemDetailUpsertRequest req, IChangeContext context, CancellationToken ct)
    {
        var ctx = (ChangeContext)context;
        ItemDetail detail;
        bool isNew;

        if (!string.IsNullOrWhiteSpace(req.Id))
        {
            var existing = item.ItemDetails.FirstOrDefault(d => d.Id == req.Id);
            if (existing == null) return ChangeHandlerResult.Failure($"ItemDetail '{req.Id}' not found on item '{item.Id}'.");
            detail = existing;
            isNew = false;
        }
        else
        {
            var match = await FindSemanticMatchAsync(item, req, ct);
            isNew = match == null;
            detail = match ?? new ItemDetail { Id = "detail-" + Guid.NewGuid() };
            if (isNew) item.ItemDetails.Add(detail);
        }

        detail.Name = req.Name;
        detail.Description = req.Description;
        if (req.Status != null) detail.Status = req.Status;
        if (req.Intent != null) detail.Intent = req.Intent;
        if (req.Hidden != null) detail.Hidden = req.Hidden.Value;
        if (req.DiscoverDc != null) detail.DiscoverDc = req.DiscoverDc;
        if (req.Origin != null) detail.Origin = req.Origin;
        if (req.TetheredToId != null) detail.TetheredToId = req.TetheredToId.Length == 0 ? null : req.TetheredToId;
        if (req.Participants != null) detail.Participants = req.Participants;
        if (req.ReviewIntervalDays != null) detail.ReviewIntervalDays = req.ReviewIntervalDays;

        var currentDay = (await context.GetCurrentTimeAsync()).TotalDaysElapsed;
        if (isNew) detail.CreatedOnDay = currentDay;
        detail.UpdatedOnDay = currentDay;

        // Detail text feeds Item.BuildEmbeddingText(), so the parent Item must be self-enriched here —
        // the incremental commit path has no post-dispatch re-embed hook (see SemanticEnrichmentHelper).
        await SemanticEnrichmentHelper.EnrichAsync(detail, embeddingService, context.Logger, ct);
        await SemanticEnrichmentHelper.EnrichAsync(item, embeddingService, context.Logger, ct);

        var participantIds = req.Participants?.Select(p => p.Id).ToList() ?? [];
        var eventId = "events/" + Guid.NewGuid();
        await context.LogEventAsync(new Event
        {
            Id = eventId,
            Summary = $"{item.Name}: detail '{detail.Name}' {(isNew ? "discovered/added" : "updated")} — {detail.Description}",
            Category = EventCategory.Interaction,
            Importance = MemoryImportance.Trivial,
            RelatedEntityId = item.Id,
            Involved = [item.Id, .. participantIds],
            LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
            DayLogged = currentDay,
            CampaignName = context.CampaignName,
        });

        if (req.Participants != null)
        {
            foreach (var participant in req.Participants)
            {
                var ku = new KnowledgeUpdate
                {
                    CharacterId = participant.Id,
                    Topic = $"itemdetail:{detail.Id}",
                    Details = $"{item.Name} — {detail.Name}: {detail.Description}",
                    Source = participant.Role == ItemDetailParticipantRole.Caused ? MemorySource.Experienced : MemorySource.Witnessed,
                    RelatedEntityIds = [item.Id],
                };
                await ctx.Dispatcher.DispatchMutationAsync(ctx, ku, ct);
            }
        }

        if (!isNew && string.IsNullOrWhiteSpace(req.Id))
        {
            // Caller didn't reference an existing detail by ID, but semantic matching folded this into
            // one anyway — a merge it didn't ask for, worth flagging (mirrors EntityCollisions).
            context.RecordMessage($"'{req.Name}' matched an existing detail on '{item.Id}' and was merged into it instead of creating a new one.");
        }

        // Hazard/Environmental details plausibly change at wildly different rates (a puncture in days,
        // a scorch mark in months) — there's no reliable signal to auto-pick the right one, so nudge
        // the LLM to decide explicitly now, while it has full context, rather than silently defaulting.
        if (detail.ReviewIntervalDays == null
            && detail.Origin?.Type is ItemDetailOriginType.Hazard or ItemDetailOriginType.Environmental)
        {
            context.RecordMessage(
                $"NOTE: '{detail.Name}' has no reviewIntervalDays set — defaulting to the engine's 60-day " +
                "staleness check. Consider setting it explicitly if this detail plausibly changes faster or " +
                "slower than that (e.g. a leak in a few days, a scorch mark in months).");
        }

        return null;
    }

    private async Task<ItemDetail?> FindSemanticMatchAsync(Item item, ItemDetailUpsertRequest req, CancellationToken ct)
    {
        var candidates = item.ItemDetails.Where(d => !d.IsRetired && d.SemanticVector != null).ToList();
        if (candidates.Count == 0) return null;

        var probeText = new ItemDetail { Name = req.Name, Description = req.Description, Status = req.Status }.BuildEmbeddingText();
        if (string.IsNullOrWhiteSpace(probeText)) return null;

        var probeVector = await embeddingService.GenerateEmbeddingAsync(probeText, ct);

        ItemDetail? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var score = SemanticEnrichmentHelper.CosineSimilarity(probeVector, candidate.SemanticVector!);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= ItemDetailSemanticMatchThreshold ? best : null;
    }

    private async Task<ChangeHandlerResult?> RetireItemDetailAsync(Item item, string detailId, IChangeContext context, CancellationToken ct)
    {
        var detail = item.ItemDetails.FirstOrDefault(d => d.Id == detailId);
        if (detail == null) return ChangeHandlerResult.Failure($"ItemDetail '{detailId}' not found on item '{item.Id}'.");

        detail.IsRetired = true;
        detail.Status = "Retired";
        var currentDay = (await context.GetCurrentTimeAsync()).TotalDaysElapsed;
        detail.UpdatedOnDay = currentDay;

        // Retired detail text is now excluded from Item.BuildEmbeddingText(); re-enrich to reflect that.
        await SemanticEnrichmentHelper.EnrichAsync(item, embeddingService, context.Logger, ct);

        await context.LogEventAsync(new Event
        {
            Id = "events/" + Guid.NewGuid(),
            Summary = $"{item.Name}: detail '{detail.Name}' retired.",
            Category = EventCategory.Interaction,
            Importance = MemoryImportance.Trivial,
            RelatedEntityId = item.Id,
            Involved = [item.Id],
            LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
            DayLogged = currentDay,
            CampaignName = context.CampaignName,
        });

        context.RecordMessage($"Retired detail '{detail.Name}' on item '{item.Id}'.");
        return null;
    }
}
