using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// The campaign's content level as the client speaks it to the server (campaign_update.contentLevel). Pure C#.
    /// The server stores it; it is a prompt-level setting (the client appends "Content: open" to the CAMPAIGN line), so what a
    /// model actually writes is still decided by its provider's policy.
    /// </summary>
    public static class ContentLevelRequest
    {
        public const string Fade = "fade";
        public const string Open = "open";

        /// <summary>Shown once per campaign while the player has never chosen.</summary>
        public const string HintText =
            "Intimate scenes fade to black by default. If your AI provider allows adult content, you can change that in Settings, under Advanced.";

        /// <summary>"fade" or "open" for any casing or padding; null for anything else.</summary>
        public static string Normalize(string level)
        {
            string v = (level ?? string.Empty).Trim().ToLowerInvariant();
            return v == Fade || v == Open ? v : null;
        }

        /// <summary>
        /// A take_turn request that changes only the content level. The server wants the player's own words for this setting; the
        /// toggle IS the player's act, so the words say so plainly. It is the commit's only change (the server insists).
        /// </summary>
        public static JsonValue Build(string level)
        {
            string wanted = Normalize(level);
            var change = JsonValue.NewObject();
            change.ObjectValue["$type"] = JsonValue.FromString("campaign_update");
            change.ObjectValue["contentLevel"] = JsonValue.FromString(wanted ?? Fade);
            change.ObjectValue["playerRequest"] = JsonValue.FromString("The player set the content level to \"" + (wanted ?? Fade) + "\" in the client's Settings.");
            var changes = JsonValue.NewArray();
            changes.ArrayValue.Add(change);
            var request = JsonValue.NewObject();
            request.ObjectValue["changes"] = changes;
            request.ObjectValue["narrative"] = JsonValue.FromString("The player changed the table's content level to " + (wanted ?? Fade) + ".");
            return request;
        }
    }
}
