namespace CampaignVault.Models;

public enum EngagementCategory
{
    Physical,
    Social,
    Medical,
    Attention,
    Proximity
}

public enum EngagementRestrictionLevel
{
    None,
    Soft,
    Hard
}

public sealed record EngagementRelationMetadata(
    EngagementCategory Category,
    EngagementRestrictionLevel RestrictionLevel,
    string DescriptionTemplate,
    string ResolutionPrompt)
{
    public EngagementRelationMetadata() : this(default!, default!, null!, null!) { }
}

public static class EngagementRelationCatalog
{
    private static readonly Dictionary<EngagementCategory, EngagementRelationMetadata> CategoryDefaults = new()
    {
        [EngagementCategory.Physical] = new(
            EngagementCategory.Physical,
            EngagementRestrictionLevel.Hard,
            "Character '{0}' is {2} '{1}'.",
            "Narrate how they attempt to escape or resolve the physical engagement in your next action."),
        [EngagementCategory.Medical] = new(
            EngagementCategory.Medical,
            EngagementRestrictionLevel.Hard,
            "Character '{0}' is {2} '{1}'.",
            "Narrate whether care continues or is interrupted in your next action."),
        [EngagementCategory.Social] = new(
            EngagementCategory.Social,
            EngagementRestrictionLevel.Soft,
            "Character '{0}' is {2} '{1}'.",
            "Narrate how the social moment resolves in your next action."),
        [EngagementCategory.Attention] = new(
            EngagementCategory.Attention,
            EngagementRestrictionLevel.None,
            "Character '{0}' is {2} '{1}'.",
            "Narrate any shift in attention in your next action."),
        [EngagementCategory.Proximity] = new(
            EngagementCategory.Proximity,
            EngagementRestrictionLevel.None,
            "Character '{0}' is {2} '{1}'.",
            "Narrate any change in spacing or tension in your next action."),
    };

    /// <summary>Legacy verb → category hints for documents that only stored relationType.</summary>
    private static readonly Dictionary<string, EngagementCategory> LegacyVerbCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grappling"] = EngagementCategory.Physical,
        ["GrappledBy"] = EngagementCategory.Physical,
        ["Restrained"] = EngagementCategory.Physical,
        ["RestrainedBy"] = EngagementCategory.Physical,
        ["Dragging"] = EngagementCategory.Physical,
        ["DraggedBy"] = EngagementCategory.Physical,
        ["Carrying"] = EngagementCategory.Physical,
        ["CarriedBy"] = EngagementCategory.Physical,
        ["Treating"] = EngagementCategory.Medical,
        ["LeaningIn"] = EngagementCategory.Social,
        ["Embracing"] = EngagementCategory.Social,
        ["Kissing"] = EngagementCategory.Social,
        ["Watching"] = EngagementCategory.Attention,
        ["WatchedBy"] = EngagementCategory.Attention,
        ["CloseProximity"] = EngagementCategory.Proximity,
    };

    private static readonly Dictionary<string, string> AsymmetricInverseVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grappling"] = "GrappledBy",
        ["GrappledBy"] = "Grappling",
        ["Restrained"] = "RestrainedBy",
        ["RestrainedBy"] = "Restrained",
        ["Dragging"] = "DraggedBy",
        ["DraggedBy"] = "Dragging",
        ["Carrying"] = "CarriedBy",
        ["CarriedBy"] = "Carrying",
        ["Watching"] = "WatchedBy",
        ["WatchedBy"] = "Watching",
    };

    public static EngagementCategory InferCategory(string verb) =>
        LegacyVerbCategories.GetValueOrDefault(verb, EngagementCategory.Physical);

    public static EngagementRelationMetadata GetMetadata(EngagementRelation relation)
    {
        var category = relation.Category;
        var defaults = CategoryDefaults[category];
        var restriction = relation.RestrictionLevel ?? defaults.RestrictionLevel;
        return defaults with { RestrictionLevel = restriction };
    }

    public static EngagementRestrictionLevel GetRestrictionLevel(EngagementRelation relation) =>
        GetMetadata(relation).RestrictionLevel;

    public static bool BlocksTravel(EngagementRelation relation) =>
        GetRestrictionLevel(relation) == EngagementRestrictionLevel.Hard;

    public static bool EmitsPressure(EngagementRelation relation) =>
        GetRestrictionLevel(relation) is EngagementRestrictionLevel.Soft or EngagementRestrictionLevel.Hard;

    public static string FormatDescription(string characterName, EngagementRelation relation)
    {
        var meta = GetMetadata(relation);
        var verbPhrase = HumanizeVerb(relation.Verb);
        return string.Format(meta.DescriptionTemplate, characterName, relation.TargetId, verbPhrase);
    }

    public static string GetInverseVerb(EngagementCategory category, string verb)
    {
        if (AsymmetricInverseVerbs.TryGetValue(verb, out var inverse))
            return inverse;

        return verb;
    }

    private static string HumanizeVerb(string verb)
    {
        if (verb.EndsWith("By", StringComparison.OrdinalIgnoreCase) && verb.Length > 2)
            return "being " + char.ToLowerInvariant(verb[0]) + verb[1..^2].ToLowerInvariant() + " by";

        return verb.ToLowerInvariant();
    }
}