using System;
using System.Collections.Generic;
using System.IO;

namespace CampaignVault.Authoring.Vault;

public static class VaultPaths
{
    public const string MetadataFileName = "vault-metadata.json";
    public const string GitIgnoreFileName = ".gitignore";
    public const string AppConfigDirectoryName = ".cv";
    public const string SyncedRefName = "refs/cv/synced";
    public const string DefaultBranchName = "main";

    public static readonly string GitIgnoreContent = """
        .cv/
        """;

    public static readonly IReadOnlyList<(string Folder, string EntityType)> EntityFolders =
    [
        ("characters", "character"),
        ("locations", "location"),
        ("quests", "quest"),
        ("factions", "faction"),
        ("lore", "lore"),
        ("rumors", "rumor"),
        ("events", "event"),
        ("items", "item")
    ];

    public static bool IsEntityRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/');
        foreach (var (folder, _) in EntityFolders)
        {
            if (normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
                && normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string? EntityTypeFromRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var normalized = relativePath.Replace('\\', '/');
        foreach (var (folder, entityType) in EntityFolders)
        {
            if (normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                return entityType;
        }

        return null;
    }
}