using System.Threading.Tasks;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class LocalEmbeddingServiceTests
{
    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsDummyArrayOfCorrectSize()
    {
        // Arrange
        var service = new LocalEmbeddingService();

        // Act
        var result = await service.GenerateEmbeddingAsync("test");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(384, result.Length);
    }
}
