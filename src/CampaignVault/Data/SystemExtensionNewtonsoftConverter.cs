using CampaignVault.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CampaignVault.Data;

/// <summary>
/// Teaches RavenDB's Newtonsoft.Json-based document serializer to round-trip
/// <see cref="SystemExtension"/>'s polymorphism, which is declared only via System.Text.Json's
/// [JsonPolymorphic]/[JsonDerivedType] attributes on SystemExtension (see Character.cs). Newtonsoft
/// has no awareness of those STJ-only attributes, so without this converter every character document
/// reloaded from RavenDB collapses SystemStats back to the base SystemExtension type, silently
/// discarding every dnd5e/pf2e-specific field (ArmorClass, ability scores, hitDie, skillModifiers,
/// ...). Worse, SystemStatsMerger.Merge deserializes into character.SystemStats.GetType(), so once a
/// character's SystemStats degrades to the base type it never recovers on a normal character_update
/// — this converter is what keeps the loaded runtime type matching what was actually stored.
///
/// Mirrors the discriminator STJ already writes on the wire ("$system": "dnd5e"/"pf2e") so raw
/// documents stay legible and consistent whether inspected via RavenDB Studio or the MCP JSON API.
/// </summary>
public sealed class SystemExtensionNewtonsoftConverter : JsonConverter
{
    private const string DiscriminatorProperty = "$system";

    public override bool CanConvert(Type objectType) => typeof(SystemExtension).IsAssignableFrom(objectType);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var jObject = JObject.Load(reader);
        var discriminator = jObject[DiscriminatorProperty]?.Value<string>();
        jObject.Remove(DiscriminatorProperty);

        var targetType = discriminator switch
        {
            RulesetSystem.Dnd5e => typeof(Dnd5eExtension),
            RulesetSystem.Pathfinder2e => typeof(Pf2eExtension),
            _ => typeof(SystemExtension)
        };

        var result = Activator.CreateInstance(targetType)
            ?? throw new InvalidOperationException($"Could not construct {targetType}.");

        // Populate does NOT re-invoke CanConvert/converter dispatch for `result`'s own type, so this
        // is safe from the infinite-recursion trap of calling back into this same converter.
        using var jsonReader = jObject.CreateReader();
        serializer.Populate(jsonReader, result);
        return result;
    }

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        // JObject.FromObject(value, serializer) would re-dispatch through this same converter
        // (CanConvert matches every SystemExtension subtype) and recurse forever — and RavenDB wraps
        // every registered converter in an internal CachingJsonConverter, so filtering `serializer`'s
        // own Converters list by reference/type doesn't reliably strip this converter back out either
        // (confirmed by a stack overflow before this fix). The parameterless overload below builds its
        // own throwaway default JsonSerializer with zero custom converters, sidestepping the whole
        // problem. Safe here because none of SystemExtension's properties (or its subtypes') rely on a
        // Newtonsoft-specific [JsonConverter] — their [JsonPropertyName]/[JsonConverter] attributes are
        // all System.Text.Json-only and Newtonsoft ignores them regardless of which serializer instance
        // is used, falling back to plain reflection either way.
        var jObject = JObject.FromObject(value);

        var discriminator = value switch
        {
            Dnd5eExtension => RulesetSystem.Dnd5e,
            Pf2eExtension => RulesetSystem.Pathfinder2e,
            _ => null
        };
        if (discriminator != null)
        {
            jObject.AddFirst(new JProperty(DiscriminatorProperty, discriminator));
        }

        jObject.WriteTo(writer);
    }
}
