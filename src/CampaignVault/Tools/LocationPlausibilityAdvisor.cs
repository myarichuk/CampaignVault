using System.Text;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Evaluates whether a location's occupancy and metadata are contextually plausible.
/// Surfaces nudges for: (1) missing staff NPCs, (2) incomplete metadata (ambientCrowd, exits, visualTags, factions, etc.).
/// Never blocks—hints only. Integrated into GetScene when partyPresent=true.
/// </summary>
internal static class LocationPlausibilityAdvisor
{
    private static readonly Dictionary<string, LocationProfile> LocationProfiles = new()
    {
        { "temple", new LocationProfile("Temple", staffRoles: ["Priest", "Healer", "Acolyte"], threshold: 1) },
        { "shrine", new LocationProfile("Shrine", staffRoles: ["Caretaker", "Priest"], threshold: 1) },
        { "tavern", new LocationProfile("Tavern", staffRoles: ["Bartender", "Proprietor", "Bard"], threshold: 1) },
        { "inn", new LocationProfile("Inn", staffRoles: ["Innkeeper", "Barkeep", "Servant"], threshold: 1) },
        { "shop", new LocationProfile("Shop", staffRoles: ["Merchant", "Shopkeeper", "Apprentice"], threshold: 1) },
        { "market", new LocationProfile("Market", staffRoles: ["Vendor", "Market Warden"], threshold: 2) },
        { "guild", new LocationProfile("Guild", staffRoles: ["Master", "Steward", "Guard"], threshold: 1) },
        { "noble house", new LocationProfile("Noble House", staffRoles: ["Lord", "Lady", "Steward", "Guard"], threshold: 2) },
        { "barracks", new LocationProfile("Barracks", staffRoles: ["Captain", "Sergeant", "Guard"], threshold: 2) },
        { "watch house", new LocationProfile("Watch House", staffRoles: ["Captain", "Guard"], threshold: 1) },
        { "library", new LocationProfile("Library", staffRoles: ["Librarian", "Scribe"], threshold: 1) },
        { "school", new LocationProfile("School", staffRoles: ["Headmaster", "Teacher", "Clerk"], threshold: 1) },
        { "blacksmith", new LocationProfile("Blacksmith", staffRoles: ["Smith", "Apprentice"], threshold: 1) },
        { "stables", new LocationProfile("Stables", staffRoles: ["Stableman", "Groom"], threshold: 1) },
        { "healer's house", new LocationProfile("Healer's House", staffRoles: ["Healer", "Herbalist"], threshold: 1) },
        { "doctor", new LocationProfile("Doctor's Office", staffRoles: ["Doctor", "Physician"], threshold: 1) },
    };

    public static string? GenerateSuggestion(Location? location, int npcCountAtLocation = 0)
    {
        if (location == null || string.IsNullOrEmpty(location.Name) || location.Type == LocationType.Region || location.Type == LocationType.Settlement)
        {
            return null;
        }

        var locName = location.Name.ToLowerInvariant();
        var locDesc = (location.Description ?? "").ToLowerInvariant();

        var sb = new StringBuilder();
        bool hasAnyIssue = false;

        // 1. Check NPC plausibility
        string? profile = FindProfile(locName, locDesc);
        if (profile != null)
        {
            var expectedStaff = LocationProfiles[profile];
            if (npcCountAtLocation < expectedStaff.Threshold &&
                !IsExplicitlyEmpty(location.Description))
            {
                if (!hasAnyIssue)
                {
                    sb.AppendLine($"\n💡 **Location Plausibility Check:** `{location.Name}`");
                    sb.AppendLine();
                    hasAnyIssue = true;
                }
                else
                {
                    sb.AppendLine();
                }

                sb.AppendLine($"**Occupancy:** This **{expectedStaff.TypeLabel}** has no staff present. Consider whether this is intentional:");
                sb.AppendLine();
                var staffCount = expectedStaff.Threshold == 1 ? "a" : expectedStaff.Threshold.ToString();
                var staffRoles = string.Join(", ", expectedStaff.StaffRoles.Take(3));
                sb.AppendLine($"  - If it *should* have staff: seed {staffCount} key NPC(s) (`world_build`, roles: {staffRoles})");
                sb.AppendLine("  - If it's empty intentionally: update description (abandoned, sealed, repurposed)");
                sb.AppendLine("  - If uncertain: what does finding it empty *mean* to the narrative?");
                sb.AppendLine();
            }
        }

        // 2. Check metadata completeness
        var metadataGaps = CheckMetadataCompleteness(location);
        if (metadataGaps.Count > 0)
        {
            if (!hasAnyIssue)
            {
                sb.AppendLine($"\n💡 **Location Metadata:** `{location.Name}`");
                sb.AppendLine();
                hasAnyIssue = true;
            }
            else
            {
                sb.AppendLine("**Metadata gaps:**");
                sb.AppendLine();
            }

            foreach (var gap in metadataGaps)
            {
                sb.AppendLine($"  - {gap}");
            }
            sb.AppendLine();
            sb.AppendLine("Use `world_build` with a location-only batch to enrich. No action needed immediately — these are optional enrichment nudges.");
            sb.AppendLine();
        }

        return hasAnyIssue ? sb.ToString() : null;
    }

    private static bool IsExplicitlyEmpty(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return false;

        return description.Contains("abandoned", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("sealed", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("destroyed", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("evacuated", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("deserted", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> CheckMetadataCompleteness(Location location)
    {
        var gaps = new List<string>();

        // AmbientCrowd
        if (string.IsNullOrEmpty(location.AmbientCrowd))
        {
            gaps.Add("**AmbientCrowd** (sensory texture at rest): \"fishermen hauling nets, merchants haggling, smell of brine\"");
        }

        // Exits
        if (location.Exits == null || location.Exits.Count == 0)
        {
            gaps.Add("**Exits** (empty array): connect to parent/sibling locations via `connectedFromLocationId`");
        }

        // VisualTags
        if (location.VisualTags == null || location.VisualTags.Count == 0)
        {
            gaps.Add("**VisualTags** (e.g., run-down, wealthy, militaristic): categorical filters for search/recall");
        }

        // DistinctiveFeatures
        if (location.DistinctiveFeatures == null || location.DistinctiveFeatures.Count == 0)
        {
            gaps.Add("**DistinctiveFeatures** (prose visual markers): \"crumbling stonework\", \"armed guards at every corner\"");
        }

        // ControllingFactionId (only for District/Building/Room)
        if ((location.Type == LocationType.District || location.Type == LocationType.Building || location.Type == LocationType.Room) &&
            string.IsNullOrEmpty(location.ControllingFactionId))
        {
            gaps.Add("**ControllingFactionId** (administrative authority): who runs this location?");
        }

        // InfluentialFactionIds (if missing/empty)
        // Note: InfluentialFactionIds may not exist on Location model yet, so we'll skip or add when field is available
        // For now, just document the gap

        // ClimateZone (if null)
        if (location.ClimateZone == null)
        {
            gaps.Add("**ClimateZone** (temperate, tropical, arctic, etc.): affects seasonal details, NPC attire, hazards");
        }

        return gaps;
    }

    private static string? FindProfile(string locName, string locDesc)
    {
        var combined = locName + " " + locDesc;

        // Try name-based matching against profile keys
        foreach (var key in LocationProfiles.Keys)
        {
            if (combined.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    private class LocationProfile
    {
        public string TypeLabel { get; }
        public List<string> StaffRoles { get; }
        public int Threshold { get; } // minimum NPCs before flagging

        public LocationProfile(string typeLabel, List<string> staffRoles, int threshold = 1)
        {
            TypeLabel = typeLabel;
            StaffRoles = staffRoles;
            Threshold = threshold;
        }
    }
}
