using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 2b: minion-link commit surface — character_create / character_update link
/// fields, dismissal clearing, and validation. Smallest focused facts over the
/// real handlers.
/// </summary>
[Collection("RavenDB")]
public class MinionLinkTests : IClassFixture<RavenDBFixture>
{
    private const string Campaign = "minion-link-test";

    private readonly RavenDBFixture _fixture;

    public MinionLinkTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static Character Caster(string id = "chars/kergil") => new()
    {
        Id = id,
        Name = "Kergil",
        CampaignName = Campaign,
        SystemStats = new Dnd5eExtension(),
    };

    private static ChangeContext CreateContext(
        IAsyncDocumentSession session,
        Dictionary<string, Character> characters) =>
        ChangeContextTestHelper.Create(
            session: session,
            characters: characters,
            campaignName: Campaign);

    private static void StubConfig(IAsyncDocumentSession session)
    {
        var keys = new CampaignDocumentKeys();
        session.LoadAsync<CampaignConfig>(keys.Config(Campaign), Arg.Any<CancellationToken>())
            .Returns(new CampaignConfig { Id = keys.Config(Campaign), ActiveSystem = RulesetSystem.Dnd5e });
    }

    [Fact]
    public async Task Create_WithLink_SetsBothFields()
    {
        // Full create path runs the character bootstrap (session queries), so this
        // one fact uses the real embedded store; the validation facts use mocks.
        using var session = _fixture.Store.OpenAsyncSession();
        var campaign = Campaign + "-" + Guid.NewGuid().ToString("N")[..8];
        var keys = new CampaignDocumentKeys();
        await session.StoreAsync(new CampaignConfig
        {
            Id = keys.Config(campaign),
            ActiveSystem = RulesetSystem.Dnd5e,
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var caster = Caster();
        caster.CampaignName = campaign;
        var minionId = "chars/skelly-" + Guid.NewGuid().ToString("N")[..8];
        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var context = ChangeContextTestHelper.Create(
            session: session,
            characters: new Dictionary<string, Character> { [caster.Id] = caster },
            campaignName: campaign);

        var result = await handler.ApplyAsync(new CharacterCreate
        {
            CharacterId = minionId,
            Name = "Skeleton",
            MaxHp = 13,
            CurrentHp = 13,
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding
            {
                ControllerId = caster.Id,
                SpellName = "animate_dead",
                Disposition = SummonDisposition.Loyal,
                DurationDays = 1,
                ExpiresAtDay = 1,
            },
        }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var created = Assert.IsType<Character>(context.Characters[minionId]);
        Assert.Equal(caster.Id, created.ControlledById);
        var binding = Assert.IsType<MinionBinding>(created.MinionBinding);
        Assert.Equal("animate_dead", binding.SpellName);
        Assert.False(binding.ControlLapsed);
    }

    [Fact]
    public async Task Create_WithLinkToMissingController_Fails()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        StubConfig(session);
        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var context = CreateContext(session, new Dictionary<string, Character>());

        var result = await handler.ApplyAsync(new CharacterCreate
        {
            CharacterId = "chars/skelly",
            Name = "Skeleton",
            MaxHp = 13,
            ControlledById = "chars/nobody",
        }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Message);
    }

    [Fact]
    public async Task Create_WithSelfLink_Fails()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        StubConfig(session);
        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var context = CreateContext(session, new Dictionary<string, Character>());

        var result = await handler.ApplyAsync(new CharacterCreate
        {
            CharacterId = "chars/skelly",
            Name = "Skeleton",
            MaxHp = 13,
            ControlledById = "chars/skelly",
        }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("itself", result.Message);
    }

    [Fact]
    public async Task Create_WithMismatchedBinding_Fails()
    {
        var caster = Caster();
        var other = Caster("chars/other");
        var session = Substitute.For<IAsyncDocumentSession>();
        StubConfig(session);
        var handler = RulesetDataTestHelper.CreateCharacterCreateHandler();
        var context = CreateContext(session, new Dictionary<string, Character>
        {
            [caster.Id] = caster,
            [other.Id] = other,
        });

        var result = await handler.ApplyAsync(new CharacterCreate
        {
            CharacterId = "chars/skelly",
            Name = "Skeleton",
            MaxHp = 13,
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding { ControllerId = other.Id },
        }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("disagrees", result.Message);
    }

    [Fact]
    public async Task Update_AddRemoveMinionIds_UnionOldestFirst()
    {
        var caster = Caster();
        caster.ControlsMinionIds.Add("chars/old");
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(caster.Id, Arg.Any<CancellationToken>()).Returns(caster);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var context = CreateContext(session, new Dictionary<string, Character>());

        var add = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = caster.Id,
            ControlsMinionIdsAdd = ["chars/new", "chars/old"],
        }, context, TestContext.Current.CancellationToken);

        Assert.True(add.Success);
        Assert.Equal(["chars/old", "chars/new"], caster.ControlsMinionIds);

        var remove = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = caster.Id,
            ControlsMinionIdsRemove = ["chars/old"],
        }, context, TestContext.Current.CancellationToken);

        Assert.True(remove.Success);
        Assert.Equal(["chars/new"], caster.ControlsMinionIds);
    }

    [Fact]
    public async Task Update_ClearMinionLink_KeepsLapsedBinding()
    {
        var caster = Caster();
        var minion = new Character
        {
            Id = "chars/skelly",
            Name = "Skeleton",
            CampaignName = Campaign,
            ControlledById = caster.Id,
            MinionBinding = new MinionBinding
            {
                ControllerId = caster.Id,
                SpellName = "animate_dead",
                Disposition = SummonDisposition.Loyal,
            },
        };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(minion.Id, Arg.Any<CancellationToken>()).Returns(minion);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var context = CreateContext(session, new Dictionary<string, Character>());

        var result = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = minion.Id,
            ClearMinionLink = true,
        }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(minion.ControlledById);
        Assert.NotNull(minion.MinionBinding);
        Assert.True(minion.MinionBinding.ControlLapsed);
        Assert.Equal(SummonDisposition.Loyal, minion.MinionBinding.Disposition);
    }

    [Fact]
    public async Task Update_ClearMinionLink_WhenAlreadyClear_Succeeds()
    {
        var plain = new Character { Id = "chars/plain", Name = "Plain", CampaignName = Campaign };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(plain.Id, Arg.Any<CancellationToken>()).Returns(plain);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var context = CreateContext(session, new Dictionary<string, Character>());

        var result = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = plain.Id,
            ClearMinionLink = true,
        }, context, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(plain.ControlledById);
        Assert.Null(plain.MinionBinding);
    }

    [Fact]
    public async Task Update_LinkMissingController_Fails()
    {
        var minion = new Character { Id = "chars/skelly", Name = "Skeleton", CampaignName = Campaign };
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Character>(minion.Id, Arg.Any<CancellationToken>()).Returns(minion);
        var handler = new CharacterUpdateHandler(new CampaignDocumentKeys(), BootstrapTestHelper.CreateOrchestrator());
        var context = CreateContext(session, new Dictionary<string, Character>());

        var result = await handler.ApplyAsync(new CharacterUpdate
        {
            CharacterId = minion.Id,
            ControlledById = "chars/nobody",
        }, context, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Message);
        Assert.Null(minion.ControlledById);
    }
}
