using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CampaignVault.Authoring.Vault;

/// <summary>
/// Shared logic for creating new campaign entities, used by both the UI and MCP tools.
/// Centralizes entity type validation, folder lookup, and slug generation.
/// </summary>
public static class EntityCreation
{
    public static bool IsSupportedEntityType(string? entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return false;
        return VaultPaths.EntityFolders.Any(f =>
            string.Equals(f.EntityType, entityType, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetFolderForType(string entityType)
    {
        var folder = VaultPaths.EntityFolders.FirstOrDefault(f =>
            string.Equals(f.EntityType, entityType, StringComparison.OrdinalIgnoreCase)).Folder;
        if (string.IsNullOrEmpty(folder))
            throw new VaultException($"Unsupported entity type '{entityType}'.");
        return folder;
    }

    public static string ToSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        return Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    public static (string RelativePath, string Slug) BuildNewEntityPath(
        string entityType,
        string name,
        DateTime timestampLocal,
        Func<string, bool>? relativePathExists = null,
        string? targetSubfolder = null)
    {
        var folder = GetFolderForType(entityType);
        if (!string.IsNullOrEmpty(targetSubfolder))
            folder = $"{folder}/{targetSubfolder}";

        var nameSlug = ToSlug(name);

        if (string.IsNullOrEmpty(nameSlug))
        {
            var ts = timestampLocal.ToString("yyyyMMddHHmmss");
            var fallbackSlug = $"new-{entityType}-{ts}";
            return ($"{folder}/{fallbackSlug}.md", fallbackSlug);
        }

        if (relativePathExists == null)
            return ($"{folder}/{nameSlug}.md", nameSlug);

        var candidateSlug = nameSlug;
        var suffix = 1;
        while (relativePathExists($"{folder}/{candidateSlug}.md"))
        {
            suffix++;
            candidateSlug = $"{nameSlug}-{suffix}";
        }

        return ($"{folder}/{candidateSlug}.md", candidateSlug);
    }
}
