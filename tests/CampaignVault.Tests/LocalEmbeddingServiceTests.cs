using CampaignVault.Services;
using Xunit;
using FluentAssertions;

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
        result.Should().NotBeNull();
        result.Length.Should().Be(384);
    }
}
