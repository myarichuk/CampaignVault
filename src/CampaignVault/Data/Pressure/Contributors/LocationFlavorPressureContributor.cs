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

        if (loc.Exits.Count > 0 && loc.Type == LocationType.Room && string.IsNullOrWhiteSpace(loc.AmbientCrowd) && !ctx.Scene.PresentNPCs.Any())
        {
            pressures.Add(new WorldPressureItem(PressureSeverity.Suggestion, loc.Id,
                $"(optional): Room has exits but no ambient hint. If this is a 'quiet' area, consider setting ambientCrowd for future visits or use schedule_change on key NPCs to anchor them here.",
                DeadEndSuggestionGroupingKey));
        }

        return Task.FromResult<IEnumerable<WorldPressureItem>>(pressures);
    }
}