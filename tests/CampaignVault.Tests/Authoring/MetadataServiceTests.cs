// tests/CampaignVault.Tests/Authoring/MetadataServiceTests.cs

using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class MetadataServiceTests
{
    [Fact]
    public async Task SaveAndLoadMetadata_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new MetadataService();
            var meta = new VaultMetadata
            {
                SchemaVersion = 1,
                CampaignName = "MyCampaign",
                CreatedAt = DateTimeOffset.UtcNow
            };

            await service.SaveMetadataAsync(tempDir, meta);

            var loaded = await service.LoadMetadataAsync(tempDir);
            Assert.NotNull(loaded);
            Assert.Equal("MyCampaign", loaded.CampaignName);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ValidateMetadata_UnsupportedSchemaVersion_Throws()
    {
        var metadata = new VaultMetadata { SchemaVersion = 99, CampaignName = "x" };
        var ex = Assert.Throws<VaultException>(() => MetadataService.ValidateMetadata(metadata));
        Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
