using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data.Context;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>The one line that lets a chase be adjudicated: everybody's speed, only when someone is off their normal pace.</summary>
public class SpeedContextTests
{
    private static Character Person(string id, float move, params (string Name, float Speed)[] effects)
    {
        var c = new Character { Id = id, Name = id, SystemStats = new Dnd5eExtension { Movement = move } };
        foreach (var (name, speed) in effects)
            c.SystemStats.StatusEffects.Add(new StatusEffect { Name = name, StatModifiers = { ["Speed"] = speed } });
        return c;
    }

    private static async Task<List<ContextItem>> Contribute(params Character[] people)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IEnumerable<string>>().ToDictionary(id => id, id => people.First(p => p.Id == id)));
        var turn = new ContextTurn
        {
            Session = session,
            CampaignName = "test",
            Config = new CampaignConfig { Id = "campaigns/test/config", ActiveSystem = "dnd5e" },
            AppliedChanges = [],
            InvolvedEntityIds = people.Select(p => p.Id).ToList(),
            Party = [people[0]],
            PresentNpcIds = people.Skip(1).Select(p => p.Id).ToList(),
        };
        return (await new SpeedContextContributor(RollModifierPipeline.BuiltIn).ContributeAsync(turn, TestContext.Current.CancellationToken)).ToList();
    }

    [Fact]
    public async Task A_hobbled_runner_and_a_pursuer_are_laid_side_by_side()
    {
        var item = Assert.Single(await Contribute(Person("chars/pc", 30, ("Hobbled", -20)), Person("chars/bandit", 30)));

        Assert.Contains("chars/pc 10 ft (30 normally)", item.Text);
        Assert.Contains("chars/bandit 30 ft", item.Text);
        Assert.Contains("chase", item.Text);
    }

    [Fact]
    public async Task Nothing_is_said_when_everyone_moves_normally_or_someone_merely_wears_armour()
    {
        Assert.Empty(await Contribute(Person("chars/pc", 30), Person("chars/bandit", 30)));

        var plate = Person("chars/pc", 30);
        plate.SystemStats.MovementModifier = -10;
        Assert.Empty(await Contribute(plate, Person("chars/bandit", 30)));
    }

    [Fact]
    public async Task The_key_changes_only_when_the_speeds_do_so_the_line_is_not_repeated()
    {
        var a = Assert.Single(await Contribute(Person("chars/pc", 30, ("Hobbled", -20)), Person("chars/bandit", 30)));
        var same = Assert.Single(await Contribute(Person("chars/pc", 30, ("Hobbled", -20)), Person("chars/bandit", 30)));
        var worse = Assert.Single(await Contribute(Person("chars/pc", 30, ("Hobbled", -25)), Person("chars/bandit", 30)));

        Assert.Equal(a.Key, same.Key);
        Assert.NotEqual(a.Key, worse.Key);
    }

    [Fact]
    public async Task A_lone_character_needs_no_comparison()
    {
        Assert.Empty(await Contribute(Person("chars/pc", 30, ("Hobbled", -20))));
    }
}
