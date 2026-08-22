using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CampaignVault.Models;

namespace CampaignVault.Rulesets;

public static class SystemStatsMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SystemExtension CreateDefault(string system) => system switch
    {
        RulesetSystem.Dnd5e => new Dnd5eExtension(),
        RulesetSystem.Pathfinder2e => new Pf2eExtension(),
        _ => new SystemExtension()
    };

    public static SystemExtension Merge(SystemExtension target, SystemExtension source, string? activeSystem = null)
    {
        // targetType serializes `target` using ITS OWN actual runtime type — that has to match
        // exactly, or SerializeToNode throws (you can't serialize an object "as" a more-derived type
        // it isn't actually an instance of). `target` can legitimately still be the base
        // SystemExtension here: a character loaded from RavenDB before SystemExtensionNewtonsoftConverter
        // existed, or one whose stored document still predates it, hasn't been upgraded yet.
        var targetType = target.GetType();

        // resultType is what the MERGE OUTPUT should be: the active ruleset's concrete type. This is
        // deliberately NOT targetType — using target.GetType() here was the actual data-loss bug:
        // if `target` was still the degraded base type, the final Deserialize below would construct a
        // plain SystemExtension and silently discard every dnd5e/pf2e-specific key (ArmorClass,
        // ability scores, hitDie, skillModifiers, ...) that DeepMerge had just correctly merged into
        // targetNode as loose JSON properties. Resolving from activeSystem instead means a single
        // correct character_update self-heals a previously-degraded character's SystemStats type,
        // rather than perpetuating the degradation on every subsequent merge.
        var resolvedSystem = activeSystem ?? GetRulesetFromType(targetType);
        var factory = CreateDefault(resolvedSystem);
        var resultType = factory.GetType();

        var targetNode = JsonSerializer.SerializeToNode(target, targetType, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize target system stats.");
        var sourceNode = JsonSerializer.SerializeToNode(source, source.GetType(), JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize source system stats.");
        var factoryNode = JsonSerializer.SerializeToNode(factory, targetType, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize factory system stats.");

        DeepMerge(targetNode, sourceNode, factoryNode);

        return JsonSerializer.Deserialize(targetNode, resultType, JsonOptions) as SystemExtension
            ?? throw new InvalidOperationException("Failed to deserialize merged system stats.");
    }

    private static string GetRulesetFromType(Type type) => type switch
    {
        _ when type == typeof(Dnd5eExtension) => RulesetSystem.Dnd5e,
        _ when type == typeof(Pf2eExtension) => RulesetSystem.Pathfinder2e,
        _ => RulesetSystem.Dnd5e
    };

    public static bool TryValidateRuleset(SystemExtension stats, string activeSystem, out string? error)
    {
        var expected = activeSystem switch
        {
            RulesetSystem.Dnd5e => typeof(Dnd5eExtension),
            RulesetSystem.Pathfinder2e => typeof(Pf2eExtension),
            _ => typeof(SystemExtension)
        };

        if (stats.GetType() == expected || stats.GetType() == typeof(SystemExtension))
        {
            error = null;
            return true;
        }

        error = $"systemStats type '{stats.GetType().Name}' does not match active ruleset '{activeSystem}'.";
        return false;
    }

    public static SystemExtension CoerceToRuleset(SystemExtension stats, string activeSystem)
    {
        if (TryValidateRuleset(stats, activeSystem, out _))
        {
            return stats;
        }

        var node = JsonSerializer.SerializeToNode(stats, stats.GetType(), JsonOptions);
        var coerced = JsonSerializer.Deserialize(node, CreateDefault(activeSystem).GetType(), JsonOptions) as SystemExtension
            ?? CreateDefault(activeSystem);

        return coerced;
    }

    private static void DeepMerge(JsonObject target, JsonObject source, JsonObject factory)
    {
        foreach (var property in source)
        {
            if (property.Value is null)
            {
                continue;
            }

            if (property.Value is JsonObject sourceObject)
            {
                var targetObject = target[property.Key] as JsonObject ?? new JsonObject();
                var factoryObject = factory[property.Key] as JsonObject ?? new JsonObject();
                DeepMerge(targetObject, sourceObject, factoryObject);
                target[property.Key] = targetObject;
                continue;
            }

            if (property.Value is JsonArray sourceArray)
            {
                if (sourceArray.Count > 0)
                {
                    target[property.Key] = sourceArray.DeepClone();
                }

                continue;
            }

            if (ValuesEqual(property.Value, factory[property.Key]))
            {
                continue;
            }

            target[property.Key] = property.Value.DeepClone();
        }
    }

    private static bool ValuesEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.ToJsonString() == right.ToJsonString();
    }
}