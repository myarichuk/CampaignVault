// tests/CampaignVault.Tests/Authoring/MetadataServiceTests.cs
using System.IO;
using System.Threading.Tasks;
using Xunit;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Models;

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
            var meta = new VaultMetadata { CampaignName = "MyCampaign" };

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
}
