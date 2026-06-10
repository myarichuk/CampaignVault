using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using Raven.Client.Documents.Session;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

public class SpatialRelationPressureTests
{
    [Fact]
    public async Task EvaluateAsync_EmitsPressure_ForGrappledCharacter()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var time = new CampaignTime();
        var config = new CampaignConfig();

        var character = new Character
        {
            Id = "characters/bram",
            Name = "Bram",
            SystemStats = new SystemExtension
            {
                SpatialRelations = new List<SpatialRelation>
                {
                    new() { TargetId = "characters/elara", RelationType = "GrappledBy" }
                }
            }
        };

        var scene = new SceneView
        {
            Location = new Location { Id = "loc_1", Name = "Room" },
            PresentNPCs = new List<NpcPresenceSummary>
            {
                new(
                    Id: character.Id,
                    Name: character.Name,
                    CurrentActivity: "Stuck",
                    CurrentMood: "Angry",
                    TopNeeds: new Dictionary<string, float>(),
                    KnownNeeds: new Dictionary<string, float>(),
                    NeedDescriptors: new Dictionary<string, string>(),
                    SystemStats: character.SystemStats
                )
            }
        };

        var ctx = new PressureContext(
            CampaignName: "test",
            Time: time,
            Config: config,
            Session: session,
            QuestDeadlines: new List<QuestDeadlineInfo>(),
            Scene: scene,
            RequestedLocationId: "loc_1",
            PartyPresent: true
        );

        var contributor = new SpatialRelationPressureContributor();
        var result = await contributor.EvaluateAsync(ctx);

        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Character:SpatialLock", list[0].GroupingKey);
        Assert.Contains("GrappledBy", list[0].Text);
    }
}
