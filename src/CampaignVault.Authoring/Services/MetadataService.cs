// src/CampaignVault.Authoring/Services/MetadataService.cs
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Vault;

namespace CampaignVault.Authoring.Services;

public class MetadataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task SaveMetadataAsync(string workspacePath, VaultMetadata metadata)
    {
        var filePath = Path.Combine(workspacePath, VaultPaths.MetadataFileName);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<VaultMetadata?> LoadMetadataAsync(string workspacePath)
    {
        var filePath = Path.Combine(workspacePath, VaultPaths.MetadataFileName);
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<VaultMetadata>(json, JsonOptions);
    }

    public static void ValidateMetadata(VaultMetadata metadata)
    {
        if (metadata.SchemaVersion != 1)
        {
            throw new VaultException(
                $"Unsupported {VaultPaths.MetadataFileName} schemaVersion {metadata.SchemaVersion}. Expected 1.");
        }

        if (string.IsNullOrWhiteSpace(metadata.CampaignName))
        {
            throw new VaultException($"{VaultPaths.MetadataFileName} is missing a campaignName.");
        }
    }
}
