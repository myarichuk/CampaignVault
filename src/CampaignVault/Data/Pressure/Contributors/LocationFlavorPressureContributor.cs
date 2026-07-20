using CampaignVault.Models;

namespace CampaignVault.Data.Pressure.Contributors;

public sealed class LocationFlavorPressureContributor : IPressureContributor
{
    public const string EmptyExpectsCrowdGroupingKey = "Location:EmptyExpectsCrowd";
    public const string EnvironmentalTagsGroupingKey = "Location:EnvironmentalTags";
    public const string FlavorVacuumGroupingKey = "Location:FlavorVacuum";
    public const string DeadEndSuggestionGroupingKey = "Location:DeadEndSuggestion";

    public PressureScope Scope => PressureScope.Scene;
    public int Order => 25;

    public Task<IEnumerable<WorldPressureItem>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        var pressures = new List<WorldPressureItem>();
        if (ctx.Scene == null || !ctx.Scene.IsLocationAnchored)
        {
            return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
        }

        var loc = ctx.Scene.Location;

        if (loc.VisualTags != null && loc.VisualTags.Any())
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, loc.Id,
                $"This location has prominent environmental tags: {string.Join(", ", loc.VisualTags)}. " +
                $"Consider how these affect visibility, travel, or danger, and narrate accordingly.",
                EnvironmentalTagsGroupingKey));
        }

        if (loc.Type != LocationType.Region && loc.PointsOfInterest.Count == 0 && string.IsNullOrWhiteSpace(loc.AmbientCrowd) && !ctx.Scene.PresentNPCs.Any())
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, loc.Id,
                $"This location lacks flavor details (no PointsOfInterest, no AmbientCrowd). " +
                "For a lively scene without DB bloat, use location_update (or include in world_build on create) to add PoIs/AmbientCrowd. Example:\n" +
                "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", " +
                "\"addPointOfInterest\": \"A half-empty mug on the bar\", \"ambientCrowd\": \"3-6 locals nursing drinks\" } ]",
                FlavorVacuumGroupingKey));
        }

        if (loc.Exits.Count > 0 && loc.Type == LocationType.Room && string.IsNullOrWhiteSpace(loc.AmbientCrowd) && loc.PointsOfInterest.Count == 0 && !ctx.Scene.PresentNPCs.Any())
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Suggestion, loc.Id,
                $"(optional): Room has exits but no ambient hint. If this is a 'quiet' area, consider setting ambientCrowd for future visits or use schedule_change on key NPCs to anchor them here.",
                DeadEndSuggestionGroupingKey));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}