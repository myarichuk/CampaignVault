using System.Text;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// Evaluates whether a location's occupancy is contextually plausible.
/// Suggests seeding staff NPCs when a location (temple, tavern, shop) is empty but shouldn't be.
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
            return null; // Regional/settlement-level places aren't expected to have staff on-site
        }

        var locName = location.Name.ToLowerInvariant();
        var locDesc = (location.Description ?? "").ToLowerInvariant();

        // Infer staffing profile from location name and description
        string? profile = FindProfile(locName, locDesc);
        if (profile == null)
        {
            return null; // No opinion on this location type
        }

        var expectedStaff = LocationProfiles[profile];

        // If the location has enough NPCs, or has notes explaining why it's empty, don't nudge
        if (npcCountAtLocation >= expectedStaff.Threshold)
        {
            return null;
        }

        // Check if description explains the emptiness
        if (!string.IsNullOrEmpty(location.Description) &&
            (location.Description.Contains("abandoned", StringComparison.OrdinalIgnoreCase) ||
             location.Description.Contains("sealed", StringComparison.OrdinalIgnoreCase) ||
             location.Description.Contains("destroyed", StringComparison.OrdinalIgnoreCase) ||
             location.Description.Contains("evacuated", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"\n💡 **Location Plausibility Check:** `{location.Name}`");
        sb.AppendLine();
        sb.AppendLine($"This **{expectedStaff.TypeLabel}** has no staff present. Consider whether this is intentional:");
        sb.AppendLine();
        sb.AppendLine("**If this location SHOULD have staff:**");
        sb.AppendLine($"1. Seed {(expectedStaff.Threshold == 1 ? "a" : expectedStaff.Threshold)} key staff NPC(s) using `world_build`:");
        sb.AppendLine($"   - Suggested roles: {string.Join(", ", expectedStaff.StaffRoles.Take(3))}");
        sb.AppendLine("   - Set `currentLocationId` to this location to anchor them here");
        sb.AppendLine("   - Add `Social` profile (role, trust, suspicion) and `Psychology` (motivation, pride, quirks)");
        sb.AppendLine("2. Or use `take_turn` → `activity` to move an existing staff NPC here from elsewhere");
        sb.AppendLine("3. Use `get_entity(locationId, partyPresent:true)` after seeding to reload the scene");
        sb.AppendLine();
        sb.AppendLine("**If this location should be empty:**");
        sb.AppendLine("- Update the location description to explain why (sealed after plague, abandoned, repurposed, etc.)");
        sb.AppendLine("- No further action needed");
        sb.AppendLine();
        sb.AppendLine("**If uncertain:**");
        sb.AppendLine("- Consider the narrative context: are the party investigating a mystery, or just passing through?");
        sb.AppendLine("- A sealed/abandoned location can still have secrets or clues — think about what finding it empty *means* to the story.");

        return sb.ToString();
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
