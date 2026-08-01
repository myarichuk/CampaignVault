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
    IReadOnlyList<string>? EnumValues);

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
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var ctx = new NullabilityInfoContext();

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

            // Get summary from [Description] or class docstring
            var descAttr = derivedType.GetCustomAttribute<DescriptionAttribute>();
            var summary = descAttr?.Description ?? $"Mutation type: {discriminator}";

            // Get fields from JSON serialization
            var typeInfo = jsonOptions.GetTypeInfo(derivedType);
            var fields = new List<CommitFieldModel>();

            if (typeInfo.Properties != null)
            {
                foreach (var prop in typeInfo.Properties)
                {
                    var jsonName = prop.Name;
                    var clrType = prop.PropertyType ?? typeof(object);

                    // Determine if required
                    var isRequired = false;
                    if (prop.AttributeProvider is PropertyInfo pi)
                    {
                        var nullInfo = ctx.Create(pi);
                        isRequired = pi.PropertyType.IsValueType ||
                            nullInfo.WriteState == NullabilityState.NotNull;
                    }

                    // Get description from [Description]
                    var fieldDesc = (prop.AttributeProvider as PropertyInfo)?
                        .GetCustomAttribute<DescriptionAttribute>()?
                        .Description;

                    // Get enum values if applicable
                    IReadOnlyList<string>? enumValues = null;
                    if (clrType.IsEnum)
                    {
                        enumValues = Enum.GetNames(clrType).ToList();
                    }

                    fields.Add(new CommitFieldModel(jsonName, clrType, isRequired, fieldDesc, enumValues));
                }
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
}
