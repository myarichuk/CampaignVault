using CampaignVault.Models;
using CampaignVault.Rulesets;

namespace CampaignVault.Data.ChangeHandlers;

public class ItemUpdateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is ItemUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var iu = (ItemUpdate)change;
        if (string.IsNullOrWhiteSpace(iu.ItemId)) return ChangeHandlerResult.Failure("itemId is required.");

        var item = context.Session != null ? await context.Session.LoadAsync<Item>(iu.ItemId, ct) : null;
        if (item == null) return ChangeHandlerResult.Failure($"Item '{iu.ItemId}' not found. Cannot update.");

        var stateBefore = item.CurrentState;
        var tagsBefore = new HashSet<string>(item.Tags);
        var featuresBefore = new HashSet<string>(item.DistinctiveFeatures);

        if (iu.NewState != null) item.CurrentState = iu.NewState;
        if (iu.CoreCategory.HasValue) item.CoreCategory = iu.CoreCategory.Value;

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

        // Item condition/appearance (singed cuffs, cracked hilt) is otherwise only recoverable from
        // conversation memory. Auto-log a low-weight history entry, mirroring Character/LocationUpdateHandler.
        var stateChanged = item.CurrentState != stateBefore
            || !tagsBefore.SetEquals(item.Tags)
            || !featuresBefore.SetEquals(item.DistinctiveFeatures);

        if (stateChanged)
        {
            var eventId = "events/" + Guid.NewGuid();
            await context.LogEventAsync(new Event
            {
                Id = eventId,
                Summary = $"{item.Name}'s state changed: {item.CurrentState ?? "(no override)"}; tags: [{string.Join(", ", item.Tags)}]",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                RelatedEntityId = iu.ItemId,
                // Event_Search indexes Involved but not RelatedEntityId — include the item ID here too
                // so this event is actually queryable via recall_history/QueryEventsAsync(involvedCharacterId:),
                // matching the existing pattern where Involved can hold non-character entity IDs.
                Involved = [iu.ItemId],
                LocationId = item.HolderId?.StartsWith("locations/", StringComparison.Ordinal) == true ? item.HolderId : null,
                DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = context.CampaignName,
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

        context.RecordMessage($"Updated state/tags for item '{iu.ItemId}'.");

        if (item.IsEquipped && (iu.PropertiesToUpsert != null || iu.PropertiesToRemove != null)
            && !string.IsNullOrEmpty(item.HolderId))
        {
            if (!context.Characters.TryGetValue(item.HolderId, out var wearer))
            {
                wearer = context.Session != null ? await context.Session.LoadAsync<Character>(item.HolderId, ct) : null;
                if (wearer != null) context.RegisterNewCharacter(wearer);
            }

            if (wearer != null)
            {
                await ArmorParameterResolver.ApplyAsync(wearer, context, ct);
                context.RecordMessage($"{wearer.Name}'s ArmorClass and WarmthRating recomputed after '{item.Name}' changed.");
            }
        }

        return ChangeHandlerResult.Ok;
    }
}
