using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Data.Templates;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Creation;

/// <summary>
/// Turns a draft into the inputs world_build takes: an un-bootstrapped <see cref="Character"/> with the system's
/// default stats and the choices written in. Generic by convention, so a new system needs no code here:
/// <list type="bullet">
/// <item>the class pick (source <c>classes</c>) sets the class/level text, <c>classLevels</c>, <c>level</c> and <c>hitDie</c>;</item>
/// <item>ability scores go to the stats fields named after each ability (<c>strength</c>, ...), without racial bonuses;</item>
/// <item>spells go to <c>spells</c>; any other step goes to the stats field named by its <c>target:</c> or key
/// (<c>race</c>, <c>background</c>, <c>feats</c>);</item>
/// <item>a step with no such field is recorded as a level-1 choice (<c>levelUpChoices</c>, one record per value),
/// which the bootstrap steps read (5e class skills).</item>
/// </list>
/// </summary>
internal static class DraftCharacterMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Character ToCharacter(
        CharacterDraft draft,
        string system,
        string id,
        IReadOnlyList<CreationStep> steps,
        CreationSources sources)
    {
        var level = Math.Max(1, draft.Level);
        var stats = SystemStatsMerger.CreateDefault(system);
        var character = new Character
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(draft.Name) ? "Unnamed" : draft.Name.Trim(),
            Notes = string.IsNullOrWhiteSpace(draft.Concept) ? null : draft.Concept.Trim(),
            CurrentAppearance = string.IsNullOrWhiteSpace(draft.Look) ? null : draft.Look.Trim(),
            IsPc = draft.Kind.Equals(RecipeCharacterCreation.PcKind, StringComparison.OrdinalIgnoreCase),
            IsPartyCompanion = draft.Kind.Equals(RecipeCharacterCreation.CompanionKind, StringComparison.OrdinalIgnoreCase),
            SystemStats = stats,
        };

        foreach (var step in steps)
        {
            if (!draft.Choices.TryGetValue(step.Key, out var value) || !draft.Has(step.Key))
                continue;

            switch (step.Kind)
            {
                case CreationStepKinds.PickOne when string.Equals(step.Source, CreationSources.Classes, StringComparison.OrdinalIgnoreCase):
                    ApplyClass(character, stats, system, draft.GetString(step.Key), level, sources);
                    break;
                case CreationStepKinds.AbilityScores:
                    foreach (var (ability, score) in draft.Get<AbilityScoreChoice>(step.Key)?.Scores ?? [])
                        TrySet(stats, ability, JsonSerializer.SerializeToElement(score, Json));
                    break;
                case CreationStepKinds.Spells:
                    TrySet(stats, step.Target ?? "spells", value);
                    break;
                case CreationStepKinds.Identity:
                    // The stat block (companions): each field to its target.
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in value.EnumerateObject())
                            TrySet(stats, field.Name, field.Value);
                    }

                    break;
                default:
                    if (!TrySet(stats, step.Target ?? step.Key, value))
                        Record(stats, step.Key, draft.GetList(step.Key), level);
                    break;
            }
        }

        return character;
    }

    private static void ApplyClass(Character character, SystemExtension stats, string system, string? className, int level, CreationSources sources)
    {
        if (string.IsNullOrWhiteSpace(className))
            return;

        var cls = sources.ClassProvider.TryResolveClass(system, className, out var def) ? def : null;
        var label = CreationSources.Label(cls?.Name ?? className);
        character.ClassLevel = $"{label} {level}";
        TrySet(stats, "classLevels", JsonSerializer.SerializeToElement(new[] { new ClassLevelEntry { Class = label, Level = level } }, Json));
        TrySet(stats, "level", JsonSerializer.SerializeToElement(level, Json));
        if (cls?.HitDie is { } hitDie)
            TrySet(stats, "hitDie", JsonSerializer.SerializeToElement(hitDie, Json));
    }

    private static void Record(SystemExtension stats, string key, IReadOnlyList<string> values, int level)
    {
        foreach (var value in values)
            stats.LevelUpChoices.Add(new LevelUpChoiceRecord { Level = level, Key = key, Value = value });
    }

    /// <summary>Writes a JSON value to the stats field with that JSON name (or property name); false when there is none or it doesn't fit.</summary>
    private static bool TrySet(SystemExtension stats, string name, JsonElement value)
    {
        var prop = stats.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.CanWrite && (
                string.Equals(p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, name, StringComparison.OrdinalIgnoreCase)
                || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (prop is null)
            return false;

        try
        {
            prop.SetValue(stats, value.Deserialize(prop.PropertyType, Json));
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
