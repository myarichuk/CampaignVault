using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class GrappleRulesetTests
{
    private ChangeContext CreateContext(params Character[] characters)
    {
        var charDict = characters.ToDictionary(c => c.Id);
        return new ChangeContext(
            sessionForTests: null,
            characters: charDict,
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location>(),
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher([], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            campaignName: null);
    }

    [Fact]
    public async Task Dnd5e_GrappleSuccess_EmitsEngagementRelationMutation()
    {
        var rollService = new FakeRollService();
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 18 });
        rollService.NextRolls.Enqueue(new RollOutcome { Result = 10 });

        var resolver = new Dnd5eRulesetResolver(rollService);
        var actor = new Character { Id = "char1", SystemStats = new Dnd5eExtension() };
        var target = new Character { Id = "char2", SystemStats = new Dnd5eExtension() };
        var context = CreateContext(actor, target);

        var action = new RulesetAction
        {
            CharacterId = "char1",
            TargetIds = ["char2"],
            ActionType = RulesetActionType.ContestedCheck,
            ActionCategory = ActionCategory.Maneuver,
            ActionName = "Grapple"
        };

        var output = await resolver.ResolveAsync(context, action);

        Assert.True(output.Result.Success);
        var mutation = Assert.Single(output.Mutations);
        var relation = Assert.IsType<EngagementRelationChange>(mutation);
        Assert.Equal(EngagementMutationHelper.GrapplingVerb, relation.Verb);
        Assert.Equal(EngagementCategory.Physical, relation.Category);
    }

    [Fact]
    public async Task Travel_DoesNotBlock_ForSoftEngagement()
    {
        var character = new Character
        {
            Id = "char_1",
            Name = "Elara",
            SystemStats = new SystemExtension
            {
                EngagementRelations =
                [
                    new EngagementRelation
                    {
                        TargetId = "char_2",
                        Category = EngagementCategory.Social,
                        Verb = "leaning in toward"
                    }
                ]
            }
        };
        var destination = new Location { Id = "loc_2", Name = "Forest" };

        var context = new ChangeContext(
            sessionForTests: null,
            characters: new Dictionary<string, Character> { { character.Id, character } },
            items: new Dictionary<string, Item>(),
            locations: new Dictionary<string, Location> { { destination.Id, destination } },
            factions: new Dictionary<string, Faction>(),
            quests: new Dictionary<string, Quest>(),
            logger: NullLogger.Instance,
            summary: [],
            dispatcher: new WorldChangeDispatcher([new TravelChangeHandler(new EncounterResolver())], new CampaignVault.Data.CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance),
            activeCombat: null,
            campaignName: null);

        var result = await new TravelChangeHandler(new EncounterResolver()).ApplyAsync(
            new TravelChange { CharacterId = "char_1", DestinationLocationId = "loc_2" },
            context);

        Assert.True(result.Success);
    }
}
