using System.ComponentModel;
using System.Reflection;
using System.Text;
using CampaignVault.Models;
using ModelContextProtocol.Server;

namespace CampaignVault.Tools;

internal static class ToolCatalog
{
    private static readonly Lazy<IReadOnlyList<ToolCatalogEntry>> CachedEntries = new(BuildEntries);

    private static readonly Dictionary<string, string> TagToCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KICKOFF TOOL"] = "Session & exploration",
        ["EXPLORATION TOOL"] = "Session & exploration",
        ["ROLEPLAY TOOL"] = "Session & exploration",
        ["UNIFIED SEARCH"] = "Session & exploration",
        ["HISTORY RECALL"] = "Session & exploration",
        ["DISCOVERABILITY TOOL"] = "Session & exploration",
        ["UNIVERSAL WRITE TOOL"] = "Mutation & time",
        ["TIME PASSAGE"] = "Mutation & time",
        ["RULES CONFIG TOOL"] = "Combat & rulesets",
        ["COMBAT TOOL"] = "Combat & rulesets",
        ["CAMPAIGN TOOL"] = "Campaign management",
        ["CAMPAIGN DISCOVERABILITY"] = "Campaign management",
        ["DEEP DIVE TOOL"] = "Deep dives",
        ["WORLD BUILDER TOOL"] = "World builder",
        ["SYSTEM DISCOVERABILITY"] = "System",
        ["TOOL CATALOG"] = "System",
    };

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
                var tag = ExtractTag(description);
                return new ToolCatalogEntry
                {
                    Name = ToSnakeCase(m.Name),
                    Category = TagToCategory.TryGetValue(tag, out var category) ? category : "Other",
                    Description = Summarize(description),
                };
            })
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractTag(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "";
        }

        var firstLine = description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var colonIndex = firstLine.IndexOf(':');
        return colonIndex >= 0 ? firstLine[..colonIndex].Trim() : firstLine.Trim();
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