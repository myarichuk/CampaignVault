using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault.Git;

namespace CampaignVault.Authoring.Vault;

public sealed class VaultBootstrap
{
    private readonly MetadataService _metadataService = new();

    public async Task<VaultMetadata> CreateAsync(string vaultPath, string campaignName, string? ruleset = null)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            throw new ArgumentException("Vault path is required.", nameof(vaultPath));

        if (string.IsNullOrWhiteSpace(campaignName))
            throw new ArgumentException("Campaign name is required.", nameof(campaignName));

        if (Directory.Exists(vaultPath) && Directory.EnumerateFileSystemEntries(vaultPath).Any())
            throw new VaultException($"Cannot create a vault in a non-empty directory: '{vaultPath}'.");

        Directory.CreateDirectory(vaultPath);
        WriteLayout(vaultPath);

        var metadata = new VaultMetadata
        {
            SchemaVersion = 1,
            CampaignName = campaignName,
            Ruleset = ruleset,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _metadataService.SaveMetadataAsync(vaultPath, metadata);

        using var git = new VaultGitRepository();
        git.Init(vaultPath, "Initialize campaign vault");

        return metadata;
    }

    public static void WriteLayout(string vaultPath)
    {
        Directory.CreateDirectory(vaultPath);
        Directory.CreateDirectory(Path.Combine(vaultPath, VaultPaths.AppConfigDirectoryName));

        foreach (var (folder, _) in VaultPaths.EntityFolders)
            Directory.CreateDirectory(Path.Combine(vaultPath, folder));

        var gitIgnorePath = Path.Combine(vaultPath, VaultPaths.GitIgnoreFileName);
        File.WriteAllText(gitIgnorePath, VaultPaths.GitIgnoreContent.ReplaceLineEndings("\n"));

        var metadataPath = Path.Combine(vaultPath, VaultPaths.MetadataFileName);
        if (!File.Exists(metadataPath))
            File.WriteAllText(metadataPath, "{}\n");
    }
}