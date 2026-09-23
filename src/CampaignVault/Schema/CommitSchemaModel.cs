using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using CampaignVault.Plugins;

namespace CampaignVault.Schema;

internal sealed record CommitFieldModel(
    string JsonName,
    Type ClrType,
    bool IsRequired,
    string? Description,
    IReadOnlyList<string>? EnumValues,
    string? RequiredHint);

internal sealed record CommitVariantModel(
    string Discriminator,
    Type ClrType,
    string Category,
    string Summary,
    IReadOnlyList<CommitFieldModel> Fields,
    bool IsHotTier,
    IReadOnlyList<string> SideEffects,
    IReadOnlyList<string> CoCommitHints,
    string? Example);

internal static class CommitSchemaModel
{
    private static readonly object VariantsGate = new();
    private static IReadOnlyList<CommitVariantModel>? _variants;
    private static int _variantsRegistryVersion = -1;

    public static IReadOnlyList<CommitVariantModel> Variants
    {
        get
        {
            lock (VariantsGate)
            {
                var version = WorldChangeTypeRegistry.Instance.Version;
                if (_variants is null || _variantsRegistryVersion != version)
                {
                    _variants = BuildVariants();
                    _variantsRegistryVersion = version;
                }

                return _variants;
            }
        }
    }

    /// <summary>Forces schema rebuild after plugin $type registration (tests / late load).</summary>
    public static void Invalidate()
    {
        lock (VariantsGate)
        {
            _variants = null;
            _variantsRegistryVersion = -1;
        }
    }

    public static CommitVariantModel? Find(string discriminator) =>
        Variants.FirstOrDefault(v => v.Discriminator == discriminator);

    private static IReadOnlyList<CommitVariantModel> BuildVariants()
    {
        var variants = new List<CommitVariantModel>();
        var worldChangeType = typeof(WorldChange);

        foreach (var (discriminator, derivedType) in WorldChangeTypeRegistry.Instance.Entries)
        {

            // Get category from [CommitCategoryAttribute]
            var categoryAttr = derivedType.GetCustomAttribute<CommitCategoryAttribute>();
            var category = categoryAttr?.Category ?? "Uncategorized";

            // Get summary from [Description] on the class
            var descAttr = derivedType.GetCustomAttribute<DescriptionAttribute>();
            var summary = descAttr?.Description ?? $"Mutation type: {discriminator}";

            // Get properties from reflection (simplified - doesn't use JSON serializer metadata)
            var fields = new List<CommitFieldModel>();
            var props = derivedType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var pi in props)
            {
                // Skip inherited properties from WorldChange base class (MinutesElapsed, IsEngineAuthored)
                if (pi.DeclaringType == worldChangeType)
                    continue;

                var jsonNameAttr = pi.GetCustomAttribute<JsonPropertyNameAttribute>();
                var jsonName = jsonNameAttr?.Name ?? pi.Name;
                var isRequired = IsRequiredProperty(pi);

                var fieldDesc = pi.GetCustomAttribute<DescriptionAttribute>()?.Description;

                IReadOnlyList<string>? enumValues = null;
                if (pi.PropertyType.IsEnum)
                {
                    enumValues = Enum.GetNames(pi.PropertyType).ToList();
                }

                var requiredHint = pi.GetCustomAttribute<CommitRequiredHintAttribute>()?.Hint;

                fields.Add(new CommitFieldModel(jsonName, pi.PropertyType, isRequired, fieldDesc, enumValues, requiredHint));
            }

            // Get attributes
            var isHotTier = derivedType.GetCustomAttribute<CommitHotTierAttribute>() != null;
            var sideEffectsAttr = derivedType.GetCustomAttribute<CommitSideEffectsAttribute>();
            var sideEffects = (IReadOnlyList<string>)(sideEffectsAttr?.Types ?? []);
            var coCommitAttr = derivedType.GetCustomAttribute<CommitCoCommitAttribute>();
            var coCommits = (IReadOnlyList<string>)(coCommitAttr?.Types ?? []);
            var exampleAttr = derivedType.GetCustomAttribute<CommitExampleAttribute>();
            var example = exampleAttr?.Json;

            variants.Add(new CommitVariantModel(
                discriminator,
                derivedType,
                category,
                summary,
                fields.AsReadOnly(),
                isHotTier,
                sideEffects,
                coCommits,
                example
            ));
        }

        return variants.AsReadOnly();
    }

    private static readonly NullabilityInfoContext NullabilityContext = new();

    /// <summary>
    /// A field is emitted as schema-required only when omitting it can never be valid: a C# <c>required</c>
    /// member, or a non-nullable reference-type scalar (typically the target id). Value types (int/bool/enum)
    /// and collections default sensibly server-side, and nullability must be read via NullabilityInfoContext —
    /// the per-property NullableAttribute is absent whenever a NullableContext covers the type, which used to
    /// mark nearly every optional string "required".
    /// </summary>
    private static bool IsRequiredProperty(PropertyInfo prop)
    {
        if (prop.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() != null)
            return true;

        var type = prop.PropertyType;
        if (type.IsValueType || type != typeof(string))
            return false;

        return NullabilityContext.Create(prop).WriteState != NullabilityState.Nullable;
    }
}
