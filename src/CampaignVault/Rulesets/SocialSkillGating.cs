using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Determines whether a ruleset action should receive relationship-based social roll modifiers.
/// Per-ruleset skill lists align with each system's native social skills while accepting
/// D&amp;D skill names when the LLM uses them cross-system.
/// </summary>
public static class SocialSkillGating
{
    // The D&D names double as the cross-system fallback vocabulary: an LLM that has internalised 5e
    // will reach for "Persuasion"/"Insight" even in a PF2e campaign, and a social check silently
    // losing its relationship modifier because of that is invisible at the table — the roll just
    // comes out wrong. So every system's set is (native skills ∪ D&D skills); the native list is
    // what keeps a PF2e "Diplomacy" or "Society" check gated correctly.
    private static readonly HashSet<string> Dnd5eSocialSkills =
        new(["Persuasion", "Deception", "Intimidation", "Insight", "Performance"],
            StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Pf2eSocialSkills =
        new(Dnd5eSocialSkills.Concat(["Diplomacy", "Society"]), StringComparer.OrdinalIgnoreCase);

    public static bool ShouldApplyRelationshipModifier(string system, RulesetAction action, string skillName)
    {
        if (action.ActionCategory == ActionCategory.Social)
        {
            return true;
        }

        var socialSkills = system switch
        {
            RulesetSystem.Pathfinder2e => Pf2eSocialSkills,
            _ => Dnd5eSocialSkills
        };

        return !string.IsNullOrWhiteSpace(skillName) && socialSkills.Contains(skillName.Trim());
    }

    /// <summary>
    /// Relationship modifiers read the target NPC's opinion of the actor.
    /// Multi-target social actions use the first target ID; set <see cref="RulesetAction.TargetIds"/>
    /// with the primary conversational partner first.
    /// </summary>
    public static string? ResolveRelationshipTargetId(RulesetAction action) =>
        action.TargetIds.FirstOrDefault();
}
