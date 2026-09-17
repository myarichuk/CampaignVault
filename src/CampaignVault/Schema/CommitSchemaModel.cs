using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;
using CampaignVault.Models;

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
    private static readonly Lazy<IReadOnlyList<CommitVariantModel>> VariantsLazy =
        new(() => BuildVariants());

    public static IReadOnlyList<CommitVariantModel> Variants => VariantsLazy.Value;

    public static CommitVariantModel? Find(string discriminator) =>
        Variants.FirstOrDefault(v => v.Discriminator == discriminator);

    private static IReadOnlyList<CommitVariantModel> BuildVariants()
    {
        var variants = new List<CommitVariantModel>();
        var worldChangeType = typeof(WorldChange);

        // Get all [JsonDerivedType] attributes from WorldChange
        var derivedTypeAttrs = worldChangeType
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToList();

        foreach (var attr in derivedTypeAttrs)
        {
            var derivedType = attr.DerivedType;
            var discriminator = attr.TypeDiscriminator as string ?? derivedType.Name;

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
                // IsNullableProperty already handles both Nullable<T> (Nullable.GetUnderlyingType) and
                // nullable reference types, so it alone determines optionality. The previous
                // `pi.PropertyType.IsValueType ||` short-circuited that check to true for every value-type
                // property — including nullable ones like `int?`/`bool?`/`EngagementCategory?`, which are
                // the overwhelming majority of WorldChange fields (almost everything here is optional) —
                // marking nearly every optional field "required" in the generated take_turn schema.
                var isRequired = !IsNullableProperty(pi);

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

    private static bool IsNullableProperty(PropertyInfo prop)
    {
        // Check if it's a nullable reference type or Nullable<T>
        if (prop.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(prop.PropertyType) != null;
        }

        var nullableAttr = prop.GetCustomAttribute<System.Runtime.CompilerServices.NullableAttribute>();
        return nullableAttr != null && nullableAttr.NullableFlags[0] != 1;
    }
}
