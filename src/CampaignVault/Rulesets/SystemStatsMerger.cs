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

    public static SystemExtension CreateDefault(RulesetSystem system) => system switch
    {
        RulesetSystem.Dnd5e => new Dnd5eExtension(),
        RulesetSystem.Pathfinder2e => new Pf2eExtension(),
        RulesetSystem.Fallout2d20 => new Fallout2d20Extension(),
        _ => new SystemExtension()
    };

    public static SystemExtension Merge(SystemExtension target, SystemExtension source)
    {
        var targetType = target.GetType();
        var factory = CreateDefault(GetRulesetFromType(targetType));
        var targetNode = JsonSerializer.SerializeToNode(target, targetType, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize target system stats.");
        var sourceNode = JsonSerializer.SerializeToNode(source, source.GetType(), JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize source system stats.");
        var factoryNode = JsonSerializer.SerializeToNode(factory, targetType, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Failed to serialize factory system stats.");

        DeepMerge(targetNode, sourceNode, factoryNode);

        return JsonSerializer.Deserialize(targetNode, targetType, JsonOptions) as SystemExtension
            ?? throw new InvalidOperationException("Failed to deserialize merged system stats.");
    }

    private static RulesetSystem GetRulesetFromType(Type type) => type switch
    {
        _ when type == typeof(Dnd5eExtension) => RulesetSystem.Dnd5e,
        _ when type == typeof(Pf2eExtension) => RulesetSystem.Pathfinder2e,
        _ when type == typeof(Fallout2d20Extension) => RulesetSystem.Fallout2d20,
        _ => RulesetSystem.Dnd5e
    };

    public static bool TryValidateRuleset(SystemExtension stats, RulesetSystem activeSystem, out string? error)
    {
        var expected = activeSystem switch
        {
            RulesetSystem.Dnd5e => typeof(Dnd5eExtension),
            RulesetSystem.Pathfinder2e => typeof(Pf2eExtension),
            RulesetSystem.Fallout2d20 => typeof(Fallout2d20Extension),
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

    public static SystemExtension CoerceToRuleset(SystemExtension stats, RulesetSystem activeSystem)
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