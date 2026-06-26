using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Authoring.Vault.Canonical;

namespace CampaignVault.Authoring.Vault;

public sealed class VaultCatalog
{
    private readonly EntityCanonicalizer _canonicalizer = new();

    public IReadOnlyList<VaultEntity> Scan(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            throw new ArgumentException("Vault path is required.", nameof(vaultPath));

        if (!Directory.Exists(vaultPath))
            throw new VaultException($"Vault directory not found: '{vaultPath}'.");

        var entities = new List<VaultEntity>();

        foreach (var (folder, entityType) in VaultPaths.EntityFolders)
        {
            var folderPath = Path.Combine(vaultPath, folder);
            if (!Directory.Exists(folderPath))
                continue;

            foreach (var absolutePath in Directory.EnumerateFiles(folderPath, "*.md", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(vaultPath, absolutePath).Replace('\\', '/');
                entities.Add(ReadEntity(relativePath, entityType, absolutePath));
            }
        }

        return entities
            .OrderBy(e => e.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private VaultEntity ReadEntity(string relativePath, string entityType, string absolutePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(absolutePath);
        }
        catch (Exception ex)
        {
            return new VaultEntity
            {
                Id = VaultFrontmatter.InferIdFromRelativePath(relativePath, entityType),
                EntityType = entityType,
                RelativePath = relativePath,
                ContentHash = string.Empty,
                HasValidFrontmatter = false,
                ParseError = ex.Message
            };
        }

        var contentHash = VaultContentHash.Compute(content);
        var hasFence = VaultFrontmatter.HasFrontmatterFence(content);
        string? parseError = null;
        var canonicalHash = string.Empty;

        if (!hasFence)
            parseError = "Missing YAML frontmatter fence.";

        string id;
        if (VaultFrontmatter.TryReadId(content, out var frontmatterId) && !string.IsNullOrWhiteSpace(frontmatterId))
        {
            id = frontmatterId!;
        }
        else
        {
            id = VaultFrontmatter.InferIdFromRelativePath(relativePath, entityType);
            if (hasFence)
                parseError ??= "Frontmatter is missing an id field.";
        }

        if (hasFence && parseError == null)
        {
            try
            {
                canonicalHash = _canonicalizer.ComputeCanonicalHash(entityType, content);
            }
            catch (Exception ex)
            {
                parseError = ex.Message;
            }
        }

        return new VaultEntity
        {
            Id = id,
            EntityType = entityType,
            RelativePath = relativePath,
            ContentHash = contentHash,
            CanonicalHash = canonicalHash,
            HasValidFrontmatter = hasFence && parseError == null,
            ParseError = parseError
        };
    }
}