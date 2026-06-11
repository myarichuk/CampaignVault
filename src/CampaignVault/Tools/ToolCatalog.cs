using System.ComponentModel;
using System.Reflection;
using System.Text;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

internal static class ToolCatalog
{
    private static readonly Lazy<IReadOnlyList<ToolCatalogEntry>> CachedEntries = new(BuildEntries);

    public static IReadOnlyList<ToolCatalogEntry> GetAll() => CachedEntries.Value;

    public static IReadOnlyList<ToolCatalogEntry> GetByCategory(string? category)
    {
        var all = CachedEntries.Value;
        if (string.IsNullOrWhiteSpace(category))
        {
            return all;
        }

        var normalized = category.Trim();
        return all
            .Where(e => e.Category.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IReadOnlyList<ToolCatalogEntry> BuildEntries()
    {
        return typeof(CampaignTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m =>
            {
                var description = m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                var category = m.GetCustomAttribute<ToolCategoryAttribute>()?.Category ?? "Other";
                return new ToolCatalogEntry
                {
                    Name = ToSnakeCase(m.Name),
                    Category = category,
                    Description = Summarize(description),
                };
            })
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Summarize(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "";
        }

        var firstLine = description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var colonIndex = firstLine.IndexOf(':');
        return colonIndex >= 0 ? firstLine[(colonIndex + 1)..].Trim() : firstLine.Trim();
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}