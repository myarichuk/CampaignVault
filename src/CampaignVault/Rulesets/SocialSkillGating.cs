using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// Determines whether a ruleset action should receive relationship-based social roll modifiers.
/// Per-ruleset skill lists align with each system's native social skills while accepting
/// D&amp;D skill names when the LLM uses them cross-system.
/// </summary>
public static class SocialSkillGating
{
    private static readonly string[] Dnd5eSocialSkills =
        ["Persuasion", "Deception", "Intimidation", "Insight", "Performance"];

    private static readonly string[] Pf2eSocialSkills =
        ["Diplomacy", "Deception", "Intimidation", "Performance", "Society"];

    private static readonly string[] FalloutSocialSkills =
        ["Speech", "Barter", "Persuasion", "Deception", "Intimidation", "Insight", "Performance"];

    public static bool ShouldApplyRelationshipModifier(RulesetSystem system, RulesetAction action, string skillName)
    {
        if (action.ActionCategory == ActionCategory.Social)
        {
            return true;
        }

        var socialSkills = system switch
        {
            RulesetSystem.Dnd5e => Dnd5eSocialSkills,
            RulesetSystem.Pathfinder2e => Pf2eSocialSkills,
            RulesetSystem.Fallout2d20 => FalloutSocialSkills,
            _ => Dnd5eSocialSkills
        };

        return socialSkills.Any(s => string.Equals(s, skillName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Relationship modifiers read the target NPC's opinion of the actor.
    /// Multi-target social actions use the first target ID; set <see cref="RulesetAction.TargetIds"/>
    /// with the primary conversational partner first.
    /// </summary>
    public static string? ResolveRelationshipTargetId(RulesetAction action) =>
        action.TargetIds.FirstOrDefault();
}