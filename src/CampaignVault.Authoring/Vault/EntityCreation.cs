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
        DateTime timestampLocal)
    {
        var folder = GetFolderForType(entityType);
        var ts = timestampLocal.ToString("yyyyMMddHHmmss");
        var nameSlug = ToSlug(name);
        var slug = string.IsNullOrEmpty(nameSlug)
            ? $"new-{entityType}-{ts}"
            : $"{nameSlug}-{ts}";
        return ($"{folder}/{slug}.md", slug);
    }
}
