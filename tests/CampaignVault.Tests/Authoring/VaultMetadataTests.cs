// tests/CampaignVault.Tests/Authoring/VaultMetadataTests.cs

using System.Text.Json;
using CampaignVault.Authoring.Models;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class VaultMetadataTests
{
    [Fact]
    public void CanSerializeAndDeserializeMetadata()
    {
        var meta = new VaultMetadata { CampaignName = "TestCampaign", RemoteHost = "localhost" };
        var json = JsonSerializer.Serialize(meta);
        var deserialized = JsonSerializer.Deserialize<VaultMetadata>(json);
        Assert.Equal("TestCampaign", deserialized?.CampaignName);
        Assert.Equal("localhost", deserialized?.RemoteHost);
    }
}