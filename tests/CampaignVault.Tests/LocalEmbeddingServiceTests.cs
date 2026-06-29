using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

public class LocalEmbeddingServiceTests
{
    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsNormalized384DimVector()
    {
        using var service = new LocalEmbeddingService();

        var result = await service.GenerateEmbeddingAsync("innkeeper at the tavern");

        Assert.NotNull(result);
        Assert.Equal(384, result.Length);
        Assert.Contains(result, v => Math.Abs(v) > 0.001f);

        var magnitude = Math.Sqrt(result.Sum(v => v * v));
        Assert.InRange(magnitude, 0.99f, 1.01f);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_ReturnsEmpty_ForBlankInput()
    {
        using var service = new LocalEmbeddingService();

        var result = await service.GenerateEmbeddingAsync("   ");

        Assert.NotNull(result);
        Assert.Empty(result);
    }
}