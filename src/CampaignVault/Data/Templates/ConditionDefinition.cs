using System.ComponentModel;

namespace CampaignVault.Data.Templates;

public record ConditionDefinition : RulesetTemplate
{
    public string System { get; init; } = default!;

    // Nullable: null means inherit from parent; explicit value overrides
    public ConditionDurationType? DurationType { get; init; }

    public List<string> Immunities { get; init; } = [];
    public List<string> Suppresses { get; init; } = [];

    [Description("One-line mechanical description surfaced to the LLM in get_system_handbook.")]
    public string? MechanicalSummary { get; init; }

    /// <summary>
    /// Advisory narrative hint only. Never auto-applied to PsychologyProfile.CurrentMood.
    /// The LLM uses this in narration at its own discretion.
    /// </summary>
    public string? MoodHint { get; init; }

    /// <summary>
    /// True for conditions that stack/have numeric levels tracked in StatusEffect.Name
    /// (e.g. dnd5e "Exhaustion 3"). On long rest, RestChangeHandler decrements these by
    /// 1 level instead of fully clearing them. Ruleset-specific: PF2e's "fatigued" is
    /// intentionally non-stacking (RAW fully clears on rest), so it stays false there.
    /// </summary>
    public bool IsStacking { get; init; }

    public static ConditionDefinition Merge(ConditionDefinition child, ConditionDefinition parent) =>
        child with
        {
            System = !string.IsNullOrEmpty(child.System) ? child.System : parent.System,
            DurationType = child.DurationType ?? parent.DurationType,
            Description = child.Description ?? parent.Description,
            MechanicalSummary = child.MechanicalSummary ?? parent.MechanicalSummary,
            MoodHint = child.MoodHint ?? parent.MoodHint,
            Immunities = child.Immunities.Count > 0 ? child.Immunities : parent.Immunities,
            Suppresses = child.Suppresses.Count > 0 ? child.Suppresses : parent.Suppresses,
            IsStacking = child.IsStacking || parent.IsStacking,
        };
}

public enum ConditionDurationType
{
    Timed,

    /// <summary>
    /// Clears on the first advance_world that moves at least one day forward.
    /// Not used by shipped SRD conditions (dnd5e/pf2e) — reserved for custom LLM-authored
    /// templates (e.g. narrative curses lasting until dawn).
    /// </summary>
    UntilDawn,

    UntilLongRest,

    /// <summary>
    /// Spell/ability concentration. Does not auto-expire by day or rest.
    /// TODO: break on damage (CON save DC 10 or half damage) — wire when combat damage pipeline can dispatch concentration checks.
    /// </summary>
    Concentration,

    Manual,
}
