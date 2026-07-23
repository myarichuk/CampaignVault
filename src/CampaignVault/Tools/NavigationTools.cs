using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

[McpServerToolType]
public class NavigationTools : CampaignToolBase, IMcpServerTool
{
    private readonly MutationTools _mutationTools;

    public NavigationTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        MutationTools mutationTools,
        ILogger<NavigationTools>? logger = null)
        : base(repository, keys, logger)
    {
        _mutationTools = mutationTools ?? throw new ArgumentNullException(nameof(mutationTools));
    }

    [ToolCategory("Movement & travel")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"NAVIGATION TOOL: Move a character to a destination location.
This is the discoverable, structured entry point for travel — prefer this over the generic commit tool for journeys.
Rolls encounter checks via the active ruleset (never invents rolls), advances time, and applies terrain/climate effects.
For local movement without travel time (same location), use commit's activity tool instead.
Requires campaignName. Example: travel_to(""chars/valen"", ""locations/highpass"", campaignName=""campaign1"")")]
    public async Task<ToolResult<CommitResult>> TravelTo(
        [Description("ID of the character traveling (e.g. 'chars/valen').")]
        string characterId,
        [Description("ID of the destination location (e.g. 'locations/highpass').")]
        string destinationLocationId,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Narrative summary of the journey (e.g. 'The party marches north through the forest'). If omitted, a default message is generated.")]
        string? narrative = null,
        [Description("Abstract modifier from -50 to +50 representing encounter risk. Negative = stealthy/cautious (e.g., Pass Without Trace, hiding). Positive = reckless/noisy (clanking armor, loud group). If omitted, assumes normal travel.")]
        int? encounterRiskModifier = null,
        [Description("Optional override for travel time in hours. If omitted, uses the LocationExit metadata between origin and destination.")]
        int? travelCostHoursOverride = null)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "characterId is required.");
        }

        if (string.IsNullOrWhiteSpace(destinationLocationId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "destinationLocationId is required.");
        }

        var travel = new TravelChange
        {
            CharacterId = characterId,
            DestinationLocationId = destinationLocationId,
            Narrative = narrative,
            EncounterRiskModifier = encounterRiskModifier,
            TravelCostHoursOverride = travelCostHoursOverride
        };

        var narrativeText = narrative ?? $"{characterId} travels to {destinationLocationId}.";

        return await _mutationTools.Commit([travel], narrativeText, campaignName);
    }

    [ToolCategory("Movement & travel")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"NAVIGATION TOOL: Rest a character at a location to recover pools and reduce needs.
This is the discoverable, structured entry point for rests — prefer this over the generic commit tool for downtime.
Rolls encounter/interruption checks via the active ruleset, recovers spell slots/abilities/hit points immediately,
and applies security modifiers (safe camp vs. dangerous location). Requires campaignName.
Example: rest_at_location(""chars/valen"", ""locations/campfire"", intendedHours=8, campaignName=""campaign1"")")]
    public async Task<ToolResult<CommitResult>> RestAtLocation(
        [Description("ID of the character resting (e.g. 'chars/valen').")]
        string characterId,
        [Description("ID of the location where they rest (e.g. 'locations/campfire').")]
        string locationId,
        [Description("How many hours the character intends to rest. E.g., 1 for short rest, 8 for long rest. Eligible resource pools (spell slots, hit dice, etc.) recover immediately.")]
        int intendedHours,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Modifier representing the safety of the rest location (-50 to +50). E.g., +20 for stealthy hidden camp, +100 for Tiny Hut, -20 for drunk in an alley. If omitted, assumes normal safety.")]
        int? securityModifier = null,
        [Description("Optional explicit rest type (LongRest, ShortRest, PerTurn). If omitted, engine infers from intendedHours: 8+ = LongRest, 1-7 = ShortRest.")]
        string? restType = null,
        [Description("Narrative description of how/where the character rests (e.g. 'Valen curls up by the fire, exhausted'). If omitted, a default message is generated.")]
        string? narrative = null)
    {
        if (string.IsNullOrWhiteSpace(characterId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "characterId is required.");
        }

        if (string.IsNullOrWhiteSpace(locationId))
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "locationId is required.");
        }

        if (intendedHours <= 0)
        {
            return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                Summary: "intendedHours must be a positive number.");
        }

        RestType? parsedRestType = null;
        if (!string.IsNullOrWhiteSpace(restType))
        {
            if (!Enum.TryParse<RestType>(restType, ignoreCase: true, out var parsed))
            {
                return new ToolResult<CommitResult>(false, Error: "InvalidInput",
                    Summary: $"restType '{restType}' is not valid. Use: LongRest, ShortRest, or PerTurn.");
            }
            parsedRestType = parsed;
        }

        var rest = new RestChange
        {
            CharacterId = characterId,
            LocationId = locationId,
            IntendedHours = intendedHours,
            SecurityModifier = securityModifier ?? 0,
            RestType = parsedRestType,
            NarrativeNote = narrative
        };

        var narrativeText = narrative ?? $"{characterId} rests at {locationId} for {intendedHours} hours.";

        return await _mutationTools.Commit([rest], narrativeText, campaignName);
    }
}
