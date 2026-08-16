using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class LocationUpdateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is LocationUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var lu = (LocationUpdate)change;
        
        if (!context.Locations.TryGetValue(lu.LocationId, out var loc))
        {
            loc = await context.Session.LoadAsync<Location>(lu.LocationId, ct);
            if (loc == null)
            {
                var hints = await context.SuggestLocationMatchAsync(lu.LocationId);
                var msg = $"Location {lu.LocationId} not found.";
                if (hints != null)
                {
                    msg += $" Did you mean: {hints}?";
                }

                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewLocation(loc);
        }

        if (lu.Name != null)
        {
            loc.Name = lu.Name;
        }

        if (lu.Description != null)
        {
            loc.Description = lu.Description;
        }

        if (lu.ParentLocationId != null)
        {
            loc.ParentLocationId = lu.ParentLocationId;
        }

        if (lu.AmbientCrowd != null)
        {
            loc.AmbientCrowd = lu.AmbientCrowd == "" ? null : lu.AmbientCrowd;
        }

        if (lu.DangerModifier.HasValue)
        {
            loc.DangerModifier = Math.Clamp(lu.DangerModifier.Value, -50, 50);
        }

        if (lu.AddExit != null)
        {
            var added = false;
            if (!loc.Exits.Any(e => e.TargetLocationId == lu.AddExit.TargetLocationId))
            {
                loc.Exits.Add(lu.AddExit);
                added = true;
            }

            if (added)
            {
                var exitTargetId = lu.AddExit.TargetLocationId;
                var exitTargetExists = context.Locations.ContainsKey(exitTargetId)
                    || (await context.Session.LoadAsync<Location>(exitTargetId, ct) != null);
                if (!exitTargetExists)
                {
                    context.RecordMessage(
                        $"Warning: exit added from '{loc.Id}' to '{exitTargetId}', but '{exitTargetId}' does not currently exist. " +
                        "This is allowed (create it before the party reaches it), but verify the ID is correct.");
                }
            }

            if (added
                && context.Config?.AutoRepairLocationConnectivity == true
                && !lu.AddExit.OneWay)
            {
                await TryAutoRepairReverseExitAsync(context, loc, lu.AddExit, ct);
            }
        }

        if (lu.RemoveExitTarget != null)
        {
            loc.Exits.RemoveAll(e => e.TargetLocationId == lu.RemoveExitTarget);
        }

        if (!string.IsNullOrWhiteSpace(lu.AddPointOfInterest))
        {
            if (!loc.PointsOfInterest.Contains(lu.AddPointOfInterest))
            {
                loc.PointsOfInterest.Add(lu.AddPointOfInterest);
            }
        }

        if (!string.IsNullOrWhiteSpace(lu.RemovePointOfInterest))
        {
            loc.PointsOfInterest.RemoveAll(p => string.Equals(p, lu.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase));
            if (loc.PointOfInterestDetails != null)
            {
                var keysToRemove = loc.PointOfInterestDetails.Keys
                    .Where(k => string.Equals(k, lu.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var k in keysToRemove)
                    loc.PointOfInterestDetails.Remove(k);
            }
        }

        if (!string.IsNullOrWhiteSpace(lu.MaterializePointOfInterest))
        {
            LocationPoiMaterializer.Apply(loc, lu.MaterializePointOfInterest, lu.PoiDetails);
        }
        
        var stateBefore = loc.CurrentState;
        var tagsBefore = new HashSet<string>(loc.VisualTags);
        var featuresBefore = new HashSet<string>(loc.DistinctiveFeatures);

        if (lu.NewState != null)
        {
            loc.CurrentState = lu.NewState;
        }

        if (lu.TagsToAdd != null)
        {
            foreach (var t in lu.TagsToAdd)
            {
                if (!loc.VisualTags.Contains(t)) loc.VisualTags.Add(t);
            }
        }

        if (lu.TagsToRemove != null)
        {
            loc.VisualTags.RemoveAll(t => lu.TagsToRemove.Contains(t));
            foreach (var removed in lu.TagsToRemove) loc.TagProvenance.Remove(removed);
        }

        if (lu.FeaturesToAdd != null)
        {
            foreach (var f in lu.FeaturesToAdd)
            {
                if (!loc.DistinctiveFeatures.Contains(f)) loc.DistinctiveFeatures.Add(f);
            }
        }

        if (lu.FeaturesToRemove != null)
        {
            loc.DistinctiveFeatures.RemoveAll(f => lu.FeaturesToRemove.Contains(f));
            foreach (var removed in lu.FeaturesToRemove) loc.TagProvenance.Remove(removed);
        }

        // Environmental/state changes (a spill, damage, mess) are otherwise only recoverable from
        // conversation memory. Auto-log a low-weight history entry, mirroring CharacterUpdateHandler,
        // so recall_history/get_scene's RecentEvents can surface *when* this changed without a second,
        // separate `event` commit for the same narrative beat.
        var stateChanged = loc.CurrentState != stateBefore
            || !tagsBefore.SetEquals(loc.VisualTags)
            || !featuresBefore.SetEquals(loc.DistinctiveFeatures);

        if (stateChanged)
        {
            var eventId = "events/" + Guid.NewGuid();
            await context.LogEventAsync(new Event
            {
                Id = eventId,
                Summary = $"{loc.Name}'s state changed: {loc.CurrentState ?? "(no override)"}; tags: [{string.Join(", ", loc.VisualTags)}]",
                Category = EventCategory.Interaction,
                Importance = MemoryImportance.Trivial,
                LocationId = lu.LocationId,
                DayLogged = (await context.GetCurrentTimeAsync()).TotalDaysElapsed,
                CampaignName = context.CampaignName,
            });

            if (loc.CurrentState != stateBefore)
            {
                if (stateBefore != null) loc.TagProvenance.Remove(stateBefore);
                if (loc.CurrentState != null) loc.TagProvenance[loc.CurrentState] = [eventId];
            }
            foreach (var addedTag in loc.VisualTags.Except(tagsBefore))
            {
                loc.TagProvenance[addedTag] = [eventId];
            }
            foreach (var addedFeature in loc.DistinctiveFeatures.Except(featuresBefore))
            {
                loc.TagProvenance[addedFeature] = [eventId];
            }
        }

        if (lu.RecordDeparture != null)
        {
            loc.RecentlyDeparted ??= [];
            loc.RecentlyDeparted.RemoveAll(d =>
                string.Equals(d.CharacterId, lu.RecordDeparture.CharacterId, StringComparison.Ordinal));
            loc.RecentlyDeparted.Insert(0, lu.RecordDeparture);
            const int maxDeparted = 10;
            if (loc.RecentlyDeparted.Count > maxDeparted)
            {
                loc.RecentlyDeparted = loc.RecentlyDeparted.Take(maxDeparted).ToList();
            }
        }

        if (lu.PointOfInterestDetails != null)
        {
            loc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in lu.PointOfInterestDetails)
            {
                var poiName = kv.Key;
                if (!loc.PointsOfInterest.Contains(poiName))
                {
                    loc.PointsOfInterest.Add(poiName);
                }
                loc.PointOfInterestDetails[poiName] = kv.Value;  // last write wins for the key
            }
        }
        
        return ChangeHandlerResult.Ok;
    }

    private static async Task TryAutoRepairReverseExitAsync(
        ChangeContext context,
        Location sourceLoc,
        LocationExit forwardExit,
        CancellationToken ct)
    {
        var targetId = forwardExit.TargetLocationId;
        if (!context.Locations.TryGetValue(targetId, out var targetLoc))
        {
            targetLoc = await context.Session.LoadAsync<Location>(targetId, ct);
            if (targetLoc == null)
            {
                return;
            }

            context.RegisterNewLocation(targetLoc);
        }

        if (targetLoc.Exits.Any(e => e.TargetLocationId == sourceLoc.Id))
        {
            return;
        }

        var reverseDesc = $"Leads back to {sourceLoc.Name}";
        targetLoc.Exits.Add(new LocationExit(sourceLoc.Id, reverseDesc));
    }
}
