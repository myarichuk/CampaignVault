using CampaignVault.Models;

namespace CampaignVault.Data.ChangeHandlers;

public class LocationCreateHandler : IWorldChangeHandler
{
    public bool ShouldHandle(WorldChange change) => change is LocationCreate;

    public async Task<ChangeHandlerResult> ApplyAsync(WorldChange change, ChangeContext context, CancellationToken ct = default)
    {
        var lc = (LocationCreate)change;
        if (string.IsNullOrWhiteSpace(lc.LocationId))
            return ChangeHandlerResult.Failure("locationId is required.");

        var existing = context.Session != null ? await context.Session.LoadAsync<Location>(lc.LocationId, ct) : null;
        Location newLoc;
        if (existing != null)
        {
            context.RecordMessage($"Warning: Location '{lc.LocationId}' already exists. Updating existing location instead of failing.");
            newLoc = existing;
            if (lc.Name != null) newLoc.Name = lc.Name;
            if (lc.Description != null) newLoc.Description = lc.Description;
            if (lc.Type != LocationType.Room) newLoc.Type = lc.Type;
            if (lc.ParentLocationId != null) newLoc.ParentLocationId = lc.ParentLocationId;
            if (lc.PointsOfInterest != null) newLoc.PointsOfInterest = lc.PointsOfInterest;
            if (lc.AmbientCrowd != null) newLoc.AmbientCrowd = lc.AmbientCrowd;
            if (lc.Exits != null) newLoc.Exits = lc.Exits;
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
                AmbientCrowd = lc.AmbientCrowd,
                Exits = lc.Exits ?? []
            };

            if (string.IsNullOrEmpty(newLoc.CampaignName))
                newLoc.CampaignName = context.CampaignName;
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

        await context.Session.StoreAsync(newLoc, ct);
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
                if (hints != null) msg += $" Did you mean: {hints}?";
                return ChangeHandlerResult.Failure(msg);
            }
            context.RegisterNewLocation(loc);
        }

        if (lu.Name != null) loc.Name = lu.Name;
        if (lu.Description != null) loc.Description = lu.Description;
        if (lu.ParentLocationId != null) loc.ParentLocationId = lu.ParentLocationId;
        if (lu.AmbientCrowd != null) loc.AmbientCrowd = lu.AmbientCrowd == "" ? null : lu.AmbientCrowd;

        if (lu.AddExit != null)
        {
            if (!loc.Exits.Any(e => e.TargetLocationId == lu.AddExit.TargetLocationId))
            {
                loc.Exits.Add(lu.AddExit);
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
        
        return ChangeHandlerResult.Ok;
    }
}
