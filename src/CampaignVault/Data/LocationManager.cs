using CampaignVault.Models;
using CampaignVault.Services;
using Raven.Client.Documents.Session;

namespace CampaignVault.Data;

public interface ILocationManager
{
    Task<Location> UpsertLocationAsync(IAsyncDocumentSession session, string campaignName, LocationUpsertRequest location);
}

internal sealed class LocationManager : ILocationManager
{
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public LocationManager(
        ILocalEmbeddingService embeddingService,
        ILogger<LocationManager> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<Location> UpsertLocationAsync(IAsyncDocumentSession session, string campaignName, LocationUpsertRequest location)
    {
        if (string.IsNullOrWhiteSpace(location.Id))
        {
            throw new ArgumentException("Location.Id is required for upsert.");
        }

        location.Id = CanonicalId.Normalize(location.Id, CanonicalId.Locations);
        var effectiveCampaignName = location.CampaignName;
        if (string.IsNullOrEmpty(effectiveCampaignName))
        {
            effectiveCampaignName = campaignName;
        }

        var existing = await session.LoadAsync<Location>(location.Id);
        var isNew = existing == null;
        Location result;
        if (existing != null)
        {
            existing.Name = location.Name;
            existing.Description = location.Description;
            existing.Type = location.Type;
            existing.ParentLocationId = location.ParentLocationId;
            existing.Exits = location.Exits ?? existing.Exits;
            existing.PointsOfInterest = location.PointsOfInterest ?? existing.PointsOfInterest;
            existing.PointOfInterestDetails = location.PointOfInterestDetails != null
                ? new Dictionary<string, string>(location.PointOfInterestDetails, StringComparer.OrdinalIgnoreCase)
                : existing.PointOfInterestDetails;
            existing.AmbientCrowd = location.AmbientCrowd;
            existing.LastVisitedDay = location.LastVisitedDay;
            existing.Metadata = location.Metadata ?? existing.Metadata;
            existing.LastUpdated = DateTime.UtcNow;
            existing.CampaignName = effectiveCampaignName;
            existing.ControllingFactionId = location.ControllingFactionId;
            existing.CurrentState = location.CurrentState;
            if (location.DangerModifier.HasValue)
            {
                existing.DangerModifier = Math.Clamp(location.DangerModifier.Value, -50, 50);
            }
            if (location.IsArchived.HasValue)
            {
                existing.IsArchived = location.IsArchived.Value;
            }
            existing.ClimateZone = location.ClimateZone ?? existing.ClimateZone;
            result = existing;
        }
        else
        {
            result = new Location
            {
                Id = location.Id,
                Name = location.Name,
                Description = location.Description,
                Type = location.Type,
                ParentLocationId = location.ParentLocationId,
                Exits = location.Exits ?? [],
                PointsOfInterest = location.PointsOfInterest ?? [],
                PointOfInterestDetails = location.PointOfInterestDetails != null
                    ? new Dictionary<string, string>(location.PointOfInterestDetails, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase),
                AmbientCrowd = location.AmbientCrowd,
                LastVisitedDay = location.LastVisitedDay,
                Metadata = location.Metadata ?? [],
                LastUpdated = DateTime.UtcNow,
                CampaignName = effectiveCampaignName,
                ControllingFactionId = location.ControllingFactionId,
                CurrentState = location.CurrentState,
                DangerModifier = Math.Clamp(location.DangerModifier ?? 0, -50, 50),
                IsArchived = location.IsArchived ?? false,
                ClimateZone = location.ClimateZone,
            };
            await session.StoreAsync(result);
        }

        if (isNew && !string.IsNullOrEmpty(location.ConnectedFromLocationId))
        {
            var parentLoc = await session.LoadAsync<Location>(location.ConnectedFromLocationId);
            if (parentLoc != null)
            {
                var connDesc = location.ConnectionDescription ?? $"Leads back to {parentLoc.Name}";

                parentLoc.Exits ??= [];
                if (!parentLoc.Exits.Any(e => e.TargetLocationId == result.Id))
                {
                    parentLoc.Exits.Add(new LocationExit(result.Id, connDesc));
                }

                result.Exits ??= [];
                if (!result.Exits.Any(e => e.TargetLocationId == parentLoc.Id))
                {
                    var revDesc = $"Leads back toward {parentLoc.Name} ({connDesc})";
                    result.Exits.Add(new LocationExit(parentLoc.Id, revDesc));
                }
            }
        }

        JsonSanitizer.Sanitize(result);
        await SemanticEnrichmentHelper.EnrichAsync(result, _embeddingService, _logger);
        return result;
    }
}
