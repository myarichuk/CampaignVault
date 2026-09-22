namespace CampaignVault.Models;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitCategoryAttribute(string category) : Attribute
{
    public string Category { get; } = category;   // "Combat" | "Narrative" | "World" | "PlotThread"
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitSideEffectsAttribute(params string[] types) : Attribute
{
    public string[] Types { get; } = types;       // discriminators this change auto-applies
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitCoCommitAttribute(params string[] types) : Attribute
{
    public string[] Types { get; } = types;
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitExampleAttribute(string json) : Attribute
{
    public string Json { get; } = json;
}

/// <summary>Marks a variant for full-detail treatment in the emitted tool schema.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class CommitHotTierAttribute : Attribute;

/// <summary>
/// Marks a field whose absence can fail validation and roll back the whole batch, even though the
/// field itself is optional/nullable (the requirement is conditional on another field's value, so it
/// can't be expressed via JSON schema's "required"). Unlike a plain [Description], this short hint is
/// always emitted in the live take_turn schema — even for cold-tier variants whose other field
/// descriptions get stripped, and without being cut by the hot-tier truncation limit — since dropping
/// or truncating it would silently hide a hard-failure condition from the model.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class CommitRequiredHintAttribute(string hint) : Attribute
{
    public string Hint { get; } = hint;
}

/// <summary>
/// Marks a WorldChange type as narrative texture only — it records what happened or how things
/// currently look/feel, but never fixes the kind of data-completeness/config issue a pressure
/// contributor nags about (missing stats, no exits, etc). An entity merely named by one of these
/// (e.g. EventOccurred.Involved, ActivityChange.CharacterId) should not have its pending pressure
/// cooldowns cleared — see CampaignRepository.StageChangesAsync. Deliberately opt-in: an
/// unattributed WorldChange type keeps the default (structural) behavior of clearing cooldowns for
/// its involved entities, so a future change type nobody thought to tag here doesn't silently lose
/// the "did the fix actually work?" recheck.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NarrativeOnlyAttribute : Attribute;
