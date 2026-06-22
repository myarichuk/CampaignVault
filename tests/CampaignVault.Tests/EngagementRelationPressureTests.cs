using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

public class EngagementRelationPressureTests
{
    [Fact]
    public async Task EvaluateAsync_EmitsPressure_ForGrappledCharacter()
    {
        var character = BuildCharacter("characters/bram", "Bram", new EngagementRelation
        {
            TargetId = "characters/elara",
            Category = EngagementCategory.Physical,
            Verb = "GrappledBy"
        });

        var result = await Evaluate(character);
        var list = result.ToList();

        Assert.Single(list);
        Assert.Equal("Character:EngagementLock", list[0].GroupingKey);
        Assert.Contains("being grappled by", list[0].Text);
    }

    [Fact]
    public async Task EvaluateAsync_EmitsPressure_ForCustomSocialVerb()
    {
        var character = BuildCharacter("characters/drunk", "Rowdy Drunk", new EngagementRelation
        {
            TargetId = "characters/pc",
            Category = EngagementCategory.Social,
            Verb = "ranting at"
        });

        var result = await Evaluate(character);
        var list = result.ToList();

        Assert.Single(list);
        Assert.Contains("ranting at", list[0].Text);
    }

    private static Character BuildCharacter(string id, string name, EngagementRelation relation) =>
        new()
        {
            Id = id,
            Name = name,
            SystemStats = new SystemExtension { EngagementRelations = [relation] }
        };

    private static async Task<IEnumerable<WorldPressureItem>> Evaluate(Character character)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        var scene = new SceneView
        {
            Location = new Location { Id = "loc_1", Name = "Tavern" },
            PresentNPCs =
            [
                new(
                    Id: character.Id,
                    Name: character.Name,
                    CurrentActivity: "Active",
                    CurrentMood: "Loud",
                    TopNeeds: new Dictionary<string, float>(),
                    KnownNeeds: new Dictionary<string, float>(),
                    NeedDescriptors: new Dictionary<string, string>(),
                    SystemStats: character.SystemStats)
            ]
        };

        var ctx = new PressureContext(
            CampaignName: "test",
            Time: new CampaignTime(),
            Config: new CampaignConfig(),
            Session: session,
            QuestDeadlines: [],
            Scene: scene,
            RequestedLocationId: "loc_1",
            PartyPresent: true);

        return await new EngagementRelationPressureContributor().EvaluateAsync(ctx);
    }
}