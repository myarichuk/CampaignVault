namespace CampaignVault.Data;

/// <summary>
/// Static catalog for PC recognition rules: skill→tag-keyword mappings and background→skill boosts.
/// Used by SceneRecognitionHintFactory to determine when a PC would recognize features based on their skills/background.
/// Mirrors the EngagementRelationCatalog pattern for consistency.
/// </summary>
public static class RecognitionRuleCatalog
{
    /// <summary>
    /// Skill proficiency threshold for triggering a recognition hint.
    /// For D&D 5e/PF2e: SkillModifier must be >= this value.
    /// </summary>
    public const int SkillThreshold = 3;

    /// <summary>
    /// Maps skill names to tag keywords that appear in Location.DistinctiveFeatures or Location.VisualTags.
    /// Matching is case-insensitive substring-based (e.g., "track" matches "animal tracks").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SkillTagMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Survival"] = new[] { "track", "trail", "scar", "wild", "overgrown", "animal", "creature", "den", "lair", "poacher", "hunter", "snare", "trap", "wilderness" },
        ["Nature"] = new[] { "plant", "animal", "creature", "wild", "overgrown", "natural", "forest", "tree", "herb", "feral", "beast", "ecology", "seasonal" },
        ["Perception"] = new[] { "hidden", "concealed", "notice", "see", "watch", "guard", "trap", "alarm", "tripwire", "lookout", "patrol", "sentry" },
        ["Insight"] = new[] { "emotion", "mood", "fear", "anger", "joy", "deception", "lie", "truth", "reaction", "tell", "secret", "heart" },
        ["History"] = new[] { "ancient", "ruin", "old", "crumble", "weathered", "monument", "inscription", "historical", "artifact", "relic", "tomb", "grave" },
        ["Arcana"] = new[] { "arcane", "magic", "spell", "rune", "mystical", "enchant", "sigil", "aura", "ward", "glyph", "portal", "crystal", "essence" },
        ["Religion"] = new[] { "shrine", "altar", "sacred", "holy", "divine", "temple", "idol", "prayer", "ritual", "cult", "faith", "blessing", "curse" },
        ["Stealth"] = new[] { "shadow", "dark", "hidden", "conceal", "sneak", "approach", "foothold", "blind", "corner", "escape", "passage", "route" },
        ["Investigation"] = new[] { "clue", "evidence", "trace", "sign", "mark", "scar", "detail", "examine", "inspect", "blood", "broken", "damage" },
    };

    /// <summary>
    /// Maps background keywords (case-insensitive substring match) to skill names that they boost.
    /// E.g., background "ranger" or "outlander" boosts Survival/Nature recognition.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BackgroundSkillBoosts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["ranger"] = new[] { "Survival", "Nature", "Perception" },
        ["outlander"] = new[] { "Survival", "Nature" },
        ["hunter"] = new[] { "Survival", "Perception" },
        ["scout"] = new[] { "Survival", "Perception" },
        ["soldier"] = new[] { "Perception", "Investigation" },
        ["sailor"] = new[] { "Perception", "Survival" },
        ["scholar"] = new[] { "History", "Arcana", "Religion" },
        ["acolyte"] = new[] { "Religion", "Insight" },
        ["sage"] = new[] { "Arcana", "History", "Investigation" },
        ["rogue"] = new[] { "Stealth", "Investigation", "Perception" },
        ["criminal"] = new[] { "Stealth", "Investigation", "Perception" },
        ["folk hero"] = new[] { "Survival", "Perception" },
        ["noble"] = new[] { "History", "Insight" },
        ["courtier"] = new[] { "Insight", "History" },
    };

    /// <summary>
    /// Determines if a PC with the given skill modifier should recognize tags.
    /// Returns true if modifier >= SkillThreshold.
    /// </summary>
    public static bool MeetsSkillThreshold(int skillModifier) => skillModifier >= SkillThreshold;

    /// <summary>
    /// Gets skill names that are boosted by the given background.
    /// Performs case-insensitive substring matching against background keyword list.
    /// </summary>
    public static IEnumerable<string> GetBoostedSkillsForBackground(string? background)
    {
        if (string.IsNullOrWhiteSpace(background))
            yield break;

        // Try exact and substring matches
        foreach (var (backgroundKeyword, skills) in BackgroundSkillBoosts)
        {
            if (background.Contains(backgroundKeyword, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var skill in skills)
                    yield return skill;
            }
        }
    }

    /// <summary>
    /// Gets tag keywords for a given skill name.
    /// </summary>
    public static IEnumerable<string> GetTagsForSkill(string skillName)
    {
        if (SkillTagMap.TryGetValue(skillName, out var tags))
        {
            foreach (var tag in tags)
                yield return tag;
        }
    }

    /// <summary>
    /// Determines if a tag (from location features) matches any keyword for the given skill.
    /// Case-insensitive substring matching.
    /// </summary>
    public static bool TagMatchesSkill(string tag, string skillName)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(skillName))
            return false;

        var keywords = GetTagsForSkill(skillName).ToList();
        return keywords.Any(keyword => tag.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
