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
