using System.Text.Json;
using System.Text.Json.Serialization;

namespace CampaignVault.Models;

public sealed class EngagementRelationJsonConverter : JsonConverter<EngagementRelation>
{
    public override EngagementRelation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("targetId", out var targetProp))
            throw new JsonException("EngagementRelation requires targetId.");

        var targetId = targetProp.GetString() ?? throw new JsonException("EngagementRelation targetId cannot be null.");

        var verb = root.TryGetProperty("verb", out var verbProp)
            ? verbProp.GetString()
            : root.TryGetProperty("relationType", out var legacyProp)
                ? legacyProp.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(verb))
            throw new JsonException("EngagementRelation requires verb or legacy relationType.");

        var category = EngagementCategory.Physical;
        if (root.TryGetProperty("category", out var categoryProp)
            && Enum.TryParse<EngagementCategory>(categoryProp.GetString(), ignoreCase: true, out var parsedCategory))
        {
            category = parsedCategory;
        }
        else
        {
            category = EngagementRelationCatalog.InferCategory(verb);
        }

        EngagementRestrictionLevel? restrictionLevel = null;
        if (root.TryGetProperty("restrictionLevel", out var restrictionProp)
            && Enum.TryParse<EngagementRestrictionLevel>(restrictionProp.GetString(), ignoreCase: true, out var parsedRestriction))
        {
            restrictionLevel = parsedRestriction;
        }

        return new EngagementRelation
        {
            TargetId = targetId,
            Category = category,
            Verb = verb,
            RestrictionLevel = restrictionLevel
        };
    }

    public override void Write(Utf8JsonWriter writer, EngagementRelation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("targetId", value.TargetId);
        writer.WriteString("category", value.Category.ToString());
        writer.WriteString("verb", value.Verb);
        if (value.RestrictionLevel.HasValue)
            writer.WriteString("restrictionLevel", value.RestrictionLevel.Value.ToString());
        writer.WriteEndObject();
    }
}