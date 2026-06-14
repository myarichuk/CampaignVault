// src/CampaignVault.Authoring/Services/MetadataService.cs
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Authoring.Services;

public class MetadataService
{
    private const string FileName = "vault-metadata.json";

    public async Task SaveMetadataAsync(string workspacePath, VaultMetadata metadata)
    {
        var filePath = Path.Combine(workspacePath, FileName);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
    }

    public async Task<VaultMetadata?> LoadMetadataAsync(string workspacePath)
    {
        var filePath = Path.Combine(workspacePath, FileName);
        if (!File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<VaultMetadata>(json);
    }
}
