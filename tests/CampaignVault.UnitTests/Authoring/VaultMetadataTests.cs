// tests/CampaignVault.Tests/Authoring/VaultMetadataTests.cs

using System;
using System.Text.Json;
using CampaignVault.Authoring.Models;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class VaultMetadataTests
{
    [Fact]
    public void CanSerializeAndDeserializeMetadata()
    {
        var meta = new VaultMetadata
        {
            SchemaVersion = 1,
            CampaignName = "TestCampaign",
            Ruleset = "Dnd5e",
            CreatedAt = DateTimeOffset.Parse("2026-06-25T12:00:00Z")
        };
        var json = JsonSerializer.Serialize(meta);
        var deserialized = JsonSerializer.Deserialize<VaultMetadata>(json);
        Assert.Equal("TestCampaign", deserialized?.CampaignName);
        Assert.Equal("Dnd5e", deserialized?.Ruleset);
        Assert.Equal(1, deserialized?.SchemaVersion);
    }
}
