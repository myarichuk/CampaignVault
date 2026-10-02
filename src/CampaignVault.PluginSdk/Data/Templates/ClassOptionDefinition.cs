using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

/// <summary>
/// One option a plugin (or the campaign) adds to a class's choice, in a file of its own: a subclass, a warlock patron, a
/// fighting style. It joins the class's progression after the layers resolve, so nothing of the class has to be restated.
/// <code>
/// name: ember_knight
/// class: fighter
/// choice: subclass          # the choice's key in the progression; "subclass" when left out
/// label: Ember Knight
/// features:
///   3: [{ name: Kindled Blade, description: ... }]
/// </code>
/// Lives in <c>RulesetData/&lt;system&gt;/classOptions/</c>. It shows with a "homebrew" tag, since it isn't shipped content.
/// </summary>
public record ClassOptionDefinition : RulesetTemplate
{
    /// <summary>The class (progression) it is an option of: its name or alias.</summary>
    public string Class { get; init; } = null!;

    /// <summary>The key of the choice it joins (<c>subclass</c>, <c>fightingStyle</c>).</summary>
    public string Choice { get; init; } = "subclass";

    /// <summary>The name players see; defaults to the template name.</summary>
    public string? Label { get; init; }

    public List<string> Skills { get; init; } = [];
    public int ExtraSkills { get; init; }
    public string? KeyAbility { get; init; }
    public List<FeatEffect> Effects { get; init; } = [];
    public Dictionary<int, List<FeatureDefinition>> Features { get; init; } = [];

    /// <summary>5e: spellcasting it gives a class without its own (see <see cref="OptionSpellcasting"/>).</summary>
    public OptionSpellcasting? Spellcasting { get; init; }

    /// <summary>What a character needs before it is offered (see <see cref="OptionPrerequisite"/>).</summary>
    public OptionPrerequisite? Prerequisite { get; init; }

    /// <summary>The option it adds, tagged homebrew when it didn't ship with the host.</summary>
    public ChoiceOption ToOption() => new()
    {
        Id = Name,
        Label = Label ?? Name,
        Description = Description,
        Skills = Skills,
        ExtraSkills = ExtraSkills,
        KeyAbility = KeyAbility,
        Effects = Effects,
        Features = Features,
        Spellcasting = Spellcasting,
        Prerequisite = Prerequisite,
        Homebrew = Homebrew,
    };

    public static ClassOptionDefinition Merge(ClassOptionDefinition child, ClassOptionDefinition parent) =>
        child with
        {
            Class = !string.IsNullOrEmpty(child.Class) ? child.Class : parent.Class,
            Label = child.Label ?? parent.Label,
            Description = child.Description ?? parent.Description,
            Skills = child.Skills.Count > 0 ? child.Skills : parent.Skills,
            ExtraSkills = child.ExtraSkills != 0 ? child.ExtraSkills : parent.ExtraSkills,
            KeyAbility = child.KeyAbility ?? parent.KeyAbility,
            Effects = child.Effects.Count > 0 ? child.Effects : parent.Effects,
            Features = child.Features.Count > 0 ? child.Features : parent.Features,
            Spellcasting = child.Spellcasting ?? parent.Spellcasting,
            Prerequisite = child.Prerequisite ?? parent.Prerequisite,
        };
}
