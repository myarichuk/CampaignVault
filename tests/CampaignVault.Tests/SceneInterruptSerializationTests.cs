using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class SceneInterruptSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Deserialize_SceneInterruptCheck_RoundTrips()
    {
        const string json = """
            [
              {
                "$type": "scene_interrupt_check",
                "locationId": "locations/training-hall",
                "characterId": "chars/valen",
                "riskModifier": 25,
                "notes": "Bloodied wanted face"
              }
            ]
            """;

        var changes = JsonSerializer.Deserialize<WorldChange[]>(json, Options);

        Assert.NotNull(changes);
        var check = Assert.IsType<SceneInterruptCheck>(changes![0]);
        Assert.Equal("locations/training-hall", check.LocationId);
        Assert.Equal("chars/valen", check.CharacterId);
        Assert.Equal(25, check.RiskModifier);
        Assert.Equal("Bloodied wanted face", check.Notes);
    }
}