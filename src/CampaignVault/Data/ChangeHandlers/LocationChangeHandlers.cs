using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class LocationCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is LocationCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var lc = (LocationCreate)change;
        if (string.IsNullOrWhiteSpace(lc.LocationId))
        {
            return ChangeHandlerResult.Failure("locationId is required.");
        }

        var existing = context.Session != null ? await context.Session.LoadAsync<Location>(lc.LocationId, ct) : null;
        Location newLoc;
        if (existing != null)
        {
            context.RecordMessage($"Warning: Location '{lc.LocationId}' already exists. Updating existing location instead of failing.");
            newLoc = existing;
            if (lc.Name != null)
            {
                newLoc.Name = lc.Name;
            }

            if (lc.Description != null)
            {
                newLoc.Description = lc.Description;
            }

            if (lc.Type != LocationType.Room)
            {
                newLoc.Type = lc.Type;
            }

            if (lc.ParentLocationId != null)
            {
                newLoc.ParentLocationId = lc.ParentLocationId;
            }

            if (lc.PointsOfInterest != null)
            {
                newLoc.PointsOfInterest = lc.PointsOfInterest;
            }

            if (!string.IsNullOrWhiteSpace(lc.RemovePointOfInterest))
            {
                newLoc.PointsOfInterest.RemoveAll(p => string.Equals(p, lc.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase));
                if (newLoc.PointOfInterestDetails != null)
                {
                    var toRemove = newLoc.PointOfInterestDetails.Keys
                        .Where(k => string.Equals(k, lc.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var k in toRemove) newLoc.PointOfInterestDetails.Remove(k);
                }
            }

            if (lc.PointOfInterestDetails != null)
            {
                newLoc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in lc.PointOfInterestDetails)
                {
                    if (!newLoc.PointsOfInterest.Contains(kv.Key))
                        newLoc.PointsOfInterest.Add(kv.Key);
                    newLoc.PointOfInterestDetails[kv.Key] = kv.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(lc.MaterializePointOfInterest))
            {
                var poiName = lc.MaterializePointOfInterest;
                if (!newLoc.PointsOfInterest.Contains(poiName))
                {
                    newLoc.PointsOfInterest.Add(poiName);
                }
                if (!string.IsNullOrWhiteSpace(lc.PoiDetails))
                {
                    newLoc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
                    var key = newLoc.PointOfInterestDetails.Keys
                        .FirstOrDefault(k => string.Equals(k, poiName, StringComparison.OrdinalIgnoreCase)) ?? poiName;
                    newLoc.PointOfInterestDetails[key] = lc.PoiDetails!;
                }
            }

            if (lc.AmbientCrowd != null)
            {
                newLoc.AmbientCrowd = lc.AmbientCrowd;
            }

            if (lc.Exits != null)
            {
                // Merge exits on partial upsert instead of full replace
                newLoc.Exits ??= [];
                foreach (var exit in lc.Exits)
                {
                    var existingExit = newLoc.Exits.FirstOrDefault(e =>
                        e.TargetLocationId == exit.TargetLocationId &&
                        e.Direction == exit.Direction);
                    if (existingExit != null)
                    {
                        newLoc.Exits.Remove(existingExit);
                    }
                    newLoc.Exits.Add(exit);
                }
            }
        }
        else
        {
            var parentId = lc.ParentLocationId ?? lc.ConnectedFromLocationId;

            newLoc = new Location
            {
                Id = lc.LocationId,
                Name = lc.Name ?? "Unnamed Location",
                Description = lc.Description ?? "",
                Type = lc.Type,
                ParentLocationId = parentId,
                PointsOfInterest = lc.PointsOfInterest ?? [],
                PointOfInterestDetails = lc.PointOfInterestDetails != null 
                    ? new Dictionary<string, string>(lc.PointOfInterestDetails, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                AmbientCrowd = lc.AmbientCrowd,
                Exits = lc.Exits ?? [],
                DangerModifier = Math.Clamp(lc.DangerModifier, -50, 50)
            };

            if (string.IsNullOrEmpty(newLoc.CampaignName))
            {
                newLoc.CampaignName = context.CampaignName;
            }

            if (lc.PointOfInterestDetails != null)
            {
                newLoc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in lc.PointOfInterestDetails)
                {
                    if (!newLoc.PointsOfInterest.Contains(kv.Key))
                        newLoc.PointsOfInterest.Add(kv.Key);
                    newLoc.PointOfInterestDetails[kv.Key] = kv.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(lc.RemovePointOfInterest))
            {
                newLoc.PointsOfInterest.RemoveAll(p => string.Equals(p, lc.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase));
                if (newLoc.PointOfInterestDetails != null)
                {
                    var toRemove = newLoc.PointOfInterestDetails.Keys
                        .Where(k => string.Equals(k, lc.RemovePointOfInterest, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var k in toRemove) newLoc.PointOfInterestDetails.Remove(k);
                }
            }

            if (!string.IsNullOrWhiteSpace(lc.MaterializePointOfInterest))
            {
                var poiName = lc.MaterializePointOfInterest;
                if (!newLoc.PointsOfInterest.Contains(poiName))
                {
                    newLoc.PointsOfInterest.Add(poiName);
                }
                if (!string.IsNullOrWhiteSpace(lc.PoiDetails))
                {
                    newLoc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
                    var key = newLoc.PointOfInterestDetails.Keys
                        .FirstOrDefault(k => string.Equals(k, poiName, StringComparison.OrdinalIgnoreCase)) ?? poiName;
                    newLoc.PointOfInterestDetails[key] = lc.PoiDetails!;
                }
            }
        }

        if (!string.IsNullOrEmpty(lc.ConnectedFromLocationId))
        {
            if (context.Locations.TryGetValue(lc.ConnectedFromLocationId, out var parentLoc))
            {
                // Per design: forward exit (on parent) uses the supplied connectionDescription.
                // Reverse exit (on child) uses derived "Leads back toward..." including the connection text.
                var connDesc = lc.ConnectionDescription ?? $"Leads back to {parentLoc.Name}";

                // Auto-Link: Parent -> Child (forward, using connection desc)
                if (!parentLoc.Exits.Any(e => e.TargetLocationId == newLoc.Id))
                {
                    parentLoc.Exits.Add(new LocationExit(newLoc.Id, connDesc));
                }

                // Auto-Link: Child -> Parent (reverse)
                if (!newLoc.Exits.Any(e => e.TargetLocationId == parentLoc.Id))
                {
                    var revDesc = $"Leads back toward {parentLoc.Name} ({connDesc})";
                    newLoc.Exits.Add(new LocationExit(parentLoc.Id, revDesc));
                }
            }
            else
            {
                context.RecordMessage($"Warning: ConnectedFromLocationId '{lc.ConnectedFromLocationId}' not found. Location created as orphan.");
            }
        }

        await context.Session!.StoreAsync(newLoc, ct);
        context.RegisterNewLocation(newLoc);

        return ChangeHandlerResult.Ok;
    }
}

public class LocationUpdateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is LocationUpdate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var lu = (LocationUpdate)change;
        
        if (!context.Locations.TryGetValue(lu.LocationId, out var loc))
        {
            loc = context.Session != null ? await context.Session.LoadAsync<Location>(lu.LocationId, ct) : null;
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
            var poiName = lu.MaterializePointOfInterest;
            if (!loc.PointsOfInterest.Contains(poiName))
            {
                loc.PointsOfInterest.Add(poiName);
            }
            if (!string.IsNullOrWhiteSpace(lu.PoiDetails))
            {
                loc.PointOfInterestDetails ??= new(StringComparer.OrdinalIgnoreCase);
                // Case-insensitive key handling for convenience
                var existingKey = loc.PointOfInterestDetails.Keys
                    .FirstOrDefault(k => string.Equals(k, poiName, StringComparison.OrdinalIgnoreCase));
                var key = existingKey ?? poiName;
                loc.PointOfInterestDetails[key] = lu.PoiDetails!;
            }
        }
        
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
            targetLoc = context.Session != null ? await context.Session.LoadAsync<Location>(targetId, ct) : null;
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
