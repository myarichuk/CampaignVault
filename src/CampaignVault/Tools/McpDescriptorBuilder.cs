using System.ComponentModel;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

/// <summary>
/// Builds MCP tool descriptor JSON from <see cref="McpServerTool"/> metadata — single source for mcps/campaign-vault/tools sync.
/// </summary>
internal static class McpDescriptorBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyDictionary<string, string> BuildAll() =>
        DiscoverTools().ToDictionary(t => t.Name, t => Serialize(t), StringComparer.OrdinalIgnoreCase);

    public static string MergePreservingNestedSchemas(string existingJson, string generatedJson)
    {
        var existing = JsonNode.Parse(existingJson) as JsonObject
                       ?? throw new InvalidOperationException("Existing descriptor is not a JSON object.");
        var generated = JsonNode.Parse(generatedJson) as JsonObject
                          ?? throw new InvalidOperationException("Generated descriptor is not a JSON object.");

        existing["description"] = generated["description"]?.DeepClone();

        if (existing["inputSchema"] is JsonObject existingSchema
            && generated["inputSchema"] is JsonObject generatedSchema
            && existingSchema["properties"] is JsonObject existingProps
            && generatedSchema["properties"] is JsonObject generatedProps)
        {
            foreach (var (key, value) in generatedProps)
            {
                if (key is "character" or "location" or "lore"
                    && existingProps[key] is JsonObject existingNested
                    && value is JsonObject generatedNested)
                {
                    var merged = generatedNested.DeepClone() as JsonObject ?? new JsonObject();
                    if (existingNested["properties"] is JsonObject nestedProps)
                    {
                        merged["properties"] = nestedProps.DeepClone();
                    }

                    existingProps[key] = merged;
                }
                else
                {
                    existingProps[key] = value?.DeepClone();
                }
            }

            if (generatedSchema["required"] is JsonArray required)
            {
                existingSchema["required"] = required.DeepClone();
            }
        }
        else
        {
            existing["inputSchema"] = generated["inputSchema"]?.DeepClone();
        }

        return existing.ToJsonString(SerializerOptions) + Environment.NewLine;
    }

    private static string Serialize(ToolDescriptor descriptor)
    {
        var root = new JsonObject
        {
            ["name"] = descriptor.Name,
            ["description"] = descriptor.Description,
            ["inputSchema"] = BuildInputSchema(descriptor),
        };

        return root.ToJsonString(SerializerOptions) + Environment.NewLine;
    }

    private static JsonObject BuildInputSchema(ToolDescriptor descriptor)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in descriptor.Parameters)
        {
            properties[parameter.Name] = BuildPropertySchema(parameter);
            if (parameter.IsRequired)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject BuildPropertySchema(ToolParameterDescriptor parameter)
    {
        var property = new JsonObject();
        if (!string.IsNullOrWhiteSpace(parameter.Description))
        {
            property["description"] = parameter.Description;
        }

        switch (parameter.Kind)
        {
            case ParameterKind.NullableString:
                property["type"] = new JsonArray("string", "null");
                if (parameter.HasDefault)
                {
                    property["default"] = null;
                }

                break;
            case ParameterKind.String:
                property["type"] = "string";
                break;
            case ParameterKind.Integer:
                property["type"] = "integer";
                break;
            case ParameterKind.Boolean:
                property["type"] = "boolean";
                if (parameter.HasDefault)
                {
                    property["default"] = parameter.DefaultValue is bool b && b;
                }

                break;
            case ParameterKind.StringArray:
                property["type"] = "array";
                property["items"] = new JsonObject { ["type"] = "string" };
                break;
            case ParameterKind.Enum:
                property["type"] = "string";
                property["enum"] = new JsonArray(parameter.EnumValues.Select(v => JsonValue.Create(v)).ToArray());
                break;
            case ParameterKind.Object:
                property["type"] = "object";
                break;
            case ParameterKind.Untyped:
                if (parameter.HasDefault)
                {
                    property["default"] = null;
                }

                break;
            case ParameterKind.Dictionary:
                property["type"] = "object";
                property["additionalProperties"] = new JsonObject { ["type"] = "string" };
                break;
        }

        return property;
    }

    private static IEnumerable<ToolDescriptor> DiscoverTools() =>
        Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m => new ToolDescriptor(
                ToolCatalog.ToSnakeCase(m.Name),
                m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                m.GetParameters().Select(BuildParameter).ToList()))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    private static ToolParameterDescriptor BuildParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var underlying = Nullable.GetUnderlyingType(type);
        var nullabilityInfo = new NullabilityInfoContext().Create(parameter);
        var isNullable = underlying != null || nullabilityInfo.WriteState == NullabilityState.Nullable;
        var effective = underlying ?? type;
        var hasDefault = parameter.HasDefaultValue;
        var defaultValue = hasDefault ? parameter.DefaultValue : null;

        var kind = effective switch
        {
            _ when effective == typeof(string) => isNullable && hasDefault && defaultValue is null
                ? ParameterKind.NullableString
                : ParameterKind.String,
            _ when effective == typeof(int) => ParameterKind.Integer,
            _ when effective == typeof(bool) => ParameterKind.Boolean,
            _ when effective == typeof(string[]) => ParameterKind.StringArray,
            _ when effective == typeof(JsonElement) || effective == typeof(JsonElement?) => ParameterKind.Untyped,
            _ when effective == typeof(Character) || effective == typeof(Location) || effective == typeof(Lore) =>
                ParameterKind.Object,
            _ when effective == typeof(Dictionary<string, string>) => ParameterKind.Dictionary,
            _ when effective.IsEnum => ParameterKind.Enum,
            _ => ParameterKind.Object,
        };

        string[] enumValues = kind == ParameterKind.Enum
            ? Enum.GetNames(effective)
            : [];

        var isRequired = kind switch
        {
            _ when hasDefault => false,
            _ when isNullable => false,
            ParameterKind.Untyped => false,
            _ => true,
        };

        return new ToolParameterDescriptor(
            parameter.Name!,
            parameter.GetCustomAttribute<DescriptionAttribute>()?.Description,
            kind,
            isRequired,
            hasDefault,
            defaultValue,
            enumValues);
    }


    private sealed record ToolDescriptor(string Name, string Description, IReadOnlyList<ToolParameterDescriptor> Parameters);

    private sealed record ToolParameterDescriptor(
        string Name,
        string? Description,
        ParameterKind Kind,
        bool IsRequired,
        bool HasDefault,
        object? DefaultValue,
        string[] EnumValues);

    private enum ParameterKind
    {
        String,
        NullableString,
        Integer,
        Boolean,
        StringArray,
        Enum,
        Object,
        Untyped,
        Dictionary,
    }
}