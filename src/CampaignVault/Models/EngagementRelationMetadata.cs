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
        ["Restraining"] = EngagementCategory.Physical,
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

    // Suffix-normalized view of LegacyVerbCategories, built once. Lets a regular inflection that isn't
    // literally one of the keys above (e.g. "restrains"/"restrained" vs. the stored "Restraining") still
    // resolve to that verb's category instead of falling through to the unmapped-verb default below.
    // Keys are a matching token, not a real English lemma (see NormalizeVerbKey) — both sides only need
    // to agree with each other, and they're built with the exact same function.
    private static readonly Dictionary<string, EngagementCategory> NormalizedVerbCategories =
        LegacyVerbCategories
            .GroupBy(kvp => NormalizeVerbKey(kvp.Key), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    // Unmapped verbs (anything outside the small legacy list above — which the LLM hits constantly,
    // since Verb is freeform and Category is optional, e.g. the field's own doc example "stitching" isn't
    // in the table) used to default to Physical/Hard: the single most disruptive category, silently
    // blocking party travel (BlocksTravel) and auto-logging an Important physical/medical event
    // (IsHistoryWorthy) for what could just as easily have been a Social/Attention beat. An unrecognized
    // verb defaults to Social/Soft instead — still visible (EmitsPressure) but never wrongly gates travel
    // or misfiles a non-physical beat as a Physical/Medical history entry. Callers that need a harder
    // restriction should pass Category explicitly.
    public static EngagementCategory InferCategory(string verb) =>
        LegacyVerbCategories.TryGetValue(verb, out var exact)
            ? exact
            : NormalizedVerbCategories.GetValueOrDefault(NormalizeVerbKey(verb), EngagementCategory.Social);

    /// <summary>
    /// Reduces a verb to a comparison key that regular English inflections of the same verb collapse
    /// onto, without a real lemmatizer: strip a trailing -ing/-ed/-es/-s, then undo the two spelling
    /// changes English regularly makes when adding those suffixes (a doubled final consonant, e.g.
    /// "dragging" -> "dragg"; a dropped silent 'e', e.g. "grappling" -> "grappl"). Deliberately narrow —
    /// irregular y/i verbs ("carry"/"carried") aren't handled. That's fine: this is a matching key
    /// between two sides built by the same function, not a claim about correct English, and it isn't
    /// worth chasing every spelling irregularity for a table this small. Add an explicit
    /// LegacyVerbCategories entry instead if an irregular form shows up in practice.
    /// </summary>
    private static string NormalizeVerbKey(string verb)
    {
        var word = verb.Trim().ToLowerInvariant();

        if (word.Length > 4 && word.EndsWith("ing", StringComparison.Ordinal))
            word = word[..^3];
        else if (word.Length > 3 && word.EndsWith("ed", StringComparison.Ordinal))
            word = word[..^2];
        else if (word.Length > 3 && word.EndsWith("es", StringComparison.Ordinal))
            word = word[..^2];
        else if (word.Length > 2 && word[^1] == 's' && !word.EndsWith("ss", StringComparison.Ordinal))
            word = word[..^1];

        if (word.Length > 2 && word[^1] == word[^2] && "aeiou".IndexOf(word[^1]) < 0)
            word = word[..^1];

        if (word.Length > 2 && word[^1] == 'e')
            word = word[..^1];

        return word;
    }

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