using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

/// <summary>
/// Generates narrative recognition hints for PCs based on their skills/background vs. location/NPC features.
/// Mirrors SceneFactionSummaryFactory and SceneNpcPresenceFactory in structure.
/// Read-time only: no persisted state, purely transient guidance for the LLM DM.
/// </summary>
internal static class SceneRecognitionHintFactory
{
    /// <summary>
    /// Creates recognition hints for PCs present in the location.
    /// Returns a list of hint strings, or empty if no PC skills/background match location/NPC tags.
    /// </summary>
    public static List<string> Create(
        Location location,
        IEnumerable<NpcPresenceSummary> presentNpcs)
    {
        var hints = new List<string>();

        // Extract PCs from presenceSummaries
        var pcs = presentNpcs.Where(npc => npc.IsPc).ToList();
        if (pcs.Count == 0)
            return hints; // No PCs present, no hints needed

        // Gather all recognizable features: location tags + NPC tags
        var allFeatures = new List<(string feature, string source)>();

        if (location.DistinctiveFeatures != null)
        {
            foreach (var feature in location.DistinctiveFeatures)
                allFeatures.Add((feature, "location"));
        }

        if (location.VisualTags != null)
        {
            foreach (var tag in location.VisualTags)
                allFeatures.Add((tag, "location"));
        }

        // Check each NPC for distinctive/visual tags
        foreach (var npc in presentNpcs.Where(n => !n.IsPc))
        {
            if (npc.DistinctiveFeatures != null)
            {
                foreach (var feature in npc.DistinctiveFeatures)
                    allFeatures.Add((feature, $"NPC: {npc.Name}"));
            }

            if (npc.VisualTags != null)
            {
                foreach (var tag in npc.VisualTags)
                    allFeatures.Add((tag, $"NPC: {npc.Name}"));
            }
        }

        if (allFeatures.Count == 0)
            return hints; // No features to recognize

        // For each PC, check if their skills/background match any features
        foreach (var pc in pcs)
        {
            var pcHints = GenerateHintsForPc(pc, allFeatures);
            hints.AddRange(pcHints);
        }

        return hints;
    }

    /// <summary>
    /// Generates recognition hints for a single PC based on matching skills/background to features.
    /// </summary>
    private static List<string> GenerateHintsForPc(
        NpcPresenceSummary pc,
        List<(string feature, string source)> features)
    {
        var hints = new List<string>();
        var systemStats = pc.SystemStats;

        if (systemStats == null)
            return hints;

        // Get skills and modifiers
        var skillModifiers = systemStats is Dnd5eExtension d5e ? d5e.SkillModifiers :
                            systemStats is Pf2eExtension pf2e ? pf2e.SkillModifiers :
                            null;

        var background = systemStats is Dnd5eExtension d5eExt ? d5eExt.Background :
                        systemStats is Pf2eExtension pf2eExt ? pf2eExt.Background :
                        null;

        // Get boosted skills from background
        var boostedSkills = RecognitionRuleCatalog.GetBoostedSkillsForBackground(background).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check each skill for matches
        if (skillModifiers != null && skillModifiers.Count > 0)
        {
            foreach (var (skillName, modifier) in skillModifiers)
            {
                // Check if this skill meets the threshold
                if (!RecognitionRuleCatalog.MeetsSkillThreshold(modifier))
                    continue;

                // Find matching features for this skill
                var matchingFeatures = features
                    .Where(f => RecognitionRuleCatalog.TagMatchesSkill(f.feature, skillName))
                    .ToList();

                if (matchingFeatures.Count == 0)
                    continue;

                // Generate hint for each matching feature
                foreach (var (feature, source) in matchingFeatures.Take(1)) // Limit to 1 hint per skill per PC to avoid spam
                {
                    var hint = ComposeHint(pc.Name, background, skillName, modifier, feature, source);
                    hints.Add(hint);
                }
            }
        }

        return hints;
    }

    /// <summary>
    /// Composes a human-readable hint string for a PC recognizing a feature.
    /// </summary>
    private static string ComposeHint(
        string pcName,
        string? background,
        string skillName,
        int skillModifier,
        string feature,
        string source)
    {
        var backgroundPart = !string.IsNullOrWhiteSpace(background) ? $" {background}" : "";
        var sourcePart = source.StartsWith("NPC:") ? $" about {source}" : "";

        return $"{pcName} ({backgroundPart}, {skillName} +{skillModifier}) would likely notice{sourcePart}: {feature}.";
    }
}
