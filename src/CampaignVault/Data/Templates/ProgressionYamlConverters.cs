using CampaignVault.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace CampaignVault.Data.Templates;

/// <summary>
/// Accepts either a bare scalar (shorthand for a feature with no description/choices, used by
/// PF2e progression files that list features as a flat string array) or a full mapping.
/// </summary>
internal sealed class FeatureDefinitionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(FeatureDefinition);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Accept<Scalar>(out var scalar))
        {
            parser.MoveNext();
            return new FeatureDefinition { Name = scalar.Value };
        }

        var raw = (Raw?)rootDeserializer(typeof(Raw));
        return new FeatureDefinition
        {
            Name = raw?.Name ?? "",
            Description = raw?.Description,
            Choices = raw?.Choices ?? [],
            Effects = raw?.Effects ?? [],
            HpPerLevel = raw?.HpPerLevel ?? 0,
            UnarmoredArmorClass = raw?.UnarmoredArmorClass,
            Spells = raw?.Spells ?? [],
            SpellOptions = raw?.SpellOptions ?? [],
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("Progression templates are read-only.");

    private sealed class Raw
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public Dictionary<string, LevelUpChoiceDefinition> Choices { get; set; } = [];
        public List<FeatEffect> Effects { get; set; } = [];
        public int HpPerLevel { get; set; }
        public UnarmoredArmorClass? UnarmoredArmorClass { get; set; }
        public Dictionary<int, List<string>> Spells { get; set; } = [];
        public Dictionary<int, List<string>> SpellOptions { get; set; } = [];
    }
}

/// <summary>
/// Accepts either a bare scalar (shorthand for an option whose id and label are the same, used by
/// feat-selection choices that just list feat ids) or a full mapping with id/label/description.
/// </summary>
internal sealed class ChoiceOptionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(ChoiceOption);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Accept<Scalar>(out var scalar))
        {
            parser.MoveNext();
            return new ChoiceOption { Id = scalar.Value, Label = scalar.Value };
        }

        var raw = (Raw?)rootDeserializer(typeof(Raw));
        return new ChoiceOption
        {
            Id = raw?.Id ?? "",
            Label = raw?.Label ?? raw?.Id ?? "",
            Description = raw?.Description,
            Skills = raw?.Skills ?? [],
            ExtraSkills = raw?.ExtraSkills ?? 0,
            KeyAbility = raw?.KeyAbility,
            Effects = raw?.Effects ?? [],
            Features = raw?.Features ?? [],
        };
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        throw new NotSupportedException("Progression templates are read-only.");

    private sealed class Raw
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public string? Description { get; set; }
        public List<string> Skills { get; set; } = [];
        public int ExtraSkills { get; set; }
        public string? KeyAbility { get; set; }
        public List<FeatEffect> Effects { get; set; } = [];
        public Dictionary<int, List<FeatureDefinition>> Features { get; set; } = [];
    }
}
