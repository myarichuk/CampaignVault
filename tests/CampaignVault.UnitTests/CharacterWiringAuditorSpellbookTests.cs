using System;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Spells recorded only on a held spellbook item, never on the sheet, are flagged.</summary>
[Collection("RavenDB")]
public class CharacterWiringAuditorSpellbookTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CharacterWiringAuditorSpellbookTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private static CharacterWiringAuditor CreateAuditor()
    {
        var (_, classes, _, feats, initializer) = RulesetDataTestHelper.CreateServices();
        return new CharacterWiringAuditor(initializer, classes, feats);
    }

    private static Character Wizard(string id, string campaign, SpellRepertoire? spells = null) => new()
    {
        Id = id,
        Name = "Maeve",
        IsPc = true,
        MaxHp = 38,
        CampaignName = campaign,
        SystemStats = new Dnd5eExtension
        {
            Level = 6,
            ClassLevels = [new ClassLevelEntry { Class = "Wizard", Level = 6 }],
            Spells = spells ?? new SpellRepertoire(),
        },
    };

    private async Task StoreSpellbookAsync(string campaign, string holderId)
    {
        using var session = _fixture.Store.OpenAsyncSession();
        await session.StoreAsync(new Item
        {
            Id = $"items/{campaign}-spellbook",
            Name = "Maeve's Spellbook",
            HolderId = holderId,
            CampaignName = campaign,
            Tags = ["spellbook", "wizard"],
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Same predicate as the auditor's query, so its auto-index exists and is caught up before the audit runs.
        await session.Query<Item>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(i => (i.CampaignName == campaign || i.CampaignName == null)
                && i.HolderId == holderId && i.Tags.Contains("spellbook"))
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SpellbookItem_WithEmptySheetSpells_FlagsSpellsOnItem()
    {
        const string campaign = "wiring-spellbook-empty";
        var maeve = Wizard($"chars/{campaign}-maeve", campaign);
        await StoreSpellbookAsync(campaign, maeve.Id);

        using var session = _fixture.Store.OpenAsyncSession();
        var findings = await CreateAuditor().AuditAsync(session, campaign, maeve, RulesetSystem.Dnd5e, null);

        var finding = Assert.Single(findings, f => f.Code == "spells_on_item");
        Assert.Contains($"items/{campaign}-spellbook", finding.Message);
    }

    [Fact]
    public async Task SpellbookItem_WithSheetSpells_IsNotFlagged()
    {
        const string campaign = "wiring-spellbook-filled";
        var maeve = Wizard($"chars/{campaign}-maeve", campaign, new SpellRepertoire { Cantrips = ["fire bolt"] });
        await StoreSpellbookAsync(campaign, maeve.Id);

        using var session = _fixture.Store.OpenAsyncSession();
        var findings = await CreateAuditor().AuditAsync(session, campaign, maeve, RulesetSystem.Dnd5e, null);

        Assert.DoesNotContain(findings, f => f.Code == "spells_on_item");
    }
}
