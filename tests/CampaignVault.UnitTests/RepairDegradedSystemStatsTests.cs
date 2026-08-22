using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Migrations;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// RepairDegradedSystemStats is the one-time repair for characters whose SystemStats collapsed to
/// the base SystemExtension type before SystemExtensionNewtonsoftConverter existed. These tests seed
/// documents shaped exactly like the corruption this repair targets and confirm it upgrades the
/// right characters (combatants in a dnd5e/pf2e campaign, currently the exact base type) while
/// leaving everything else — narrative-system characters, already-correctly-typed ones, non-combatant
/// characters — untouched.
/// </summary>
[Collection("RavenDB")]
public class RepairDegradedSystemStatsTests
{
    private readonly RavenDbTestEnvironment _environment;
    private readonly CampaignDocumentKeys _keys = new();

    public RepairDegradedSystemStatsTests(RavenDbTestEnvironment environment)
    {
        _environment = environment;
    }

    [Fact]
    public async Task Repair_UpgradesDegradedCombatant_InDnd5eCampaign()
    {
        var (store, _) = _environment.CreateStoreForClass($"SystemStatsRepair_{Guid.NewGuid():N}");
        const string campaign = "repair-test-campaign";
        var charId = "chars/repair-test-npc";

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig
            {
                Id = _keys.Config(campaign),
                ActiveSystem = RulesetSystem.Dnd5e,
            });
            await session.StoreAsync(new Character
            {
                Id = charId,
                Name = "Degraded NPC",
                CampaignName = campaign,
                KeepAlive = true,
                SystemStats = new SystemExtension { Willpower = 55 }, // exact base type — the corruption shape
            }, charId);
            await session.SaveChangesAsync();
        }

        // The embedded (non-Docker) test fallback shares one RavenDB database across every test
        // class, so `repaired` reflects whatever else is in that shared database at the moment —
        // assert on this test's own character by id, not the aggregate count.
        var repair = new RepairDegradedSystemStats(store);
        var (_, details) = await repair.ExecuteAsync();

        Assert.Contains(details, d => d.Contains(charId));

        using var readSession = store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(charId);
        var stats = Assert.IsType<Dnd5eExtension>(reloaded!.SystemStats);
        Assert.Equal(55, stats.Willpower); // base-class field preserved, not reset
        Assert.Equal(10, stats.ArmorClass); // ruleset-specific field: was already lost, now at type default
    }

    [Fact]
    public async Task Repair_LeavesNarrativeSystemCharacter_Untouched()
    {
        var (store, _) = _environment.CreateStoreForClass($"SystemStatsRepair_{Guid.NewGuid():N}");
        const string campaign = "repair-test-narrative-campaign";
        var charId = "chars/repair-test-narrative-npc";

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig
            {
                Id = _keys.Config(campaign),
                ActiveSystem = RulesetSystem.Narrative,
            });
            await session.StoreAsync(new Character
            {
                Id = charId,
                Name = "Narrative NPC",
                CampaignName = campaign,
                KeepAlive = true,
                SystemStats = new SystemExtension(),
            }, charId);
            await session.SaveChangesAsync();
        }

        var repair = new RepairDegradedSystemStats(store);
        var (_, details) = await repair.ExecuteAsync();

        Assert.DoesNotContain(details, d => d.Contains(charId));

        using var readSession = store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(charId);
        Assert.IsType<SystemExtension>(reloaded!.SystemStats);
    }

    [Fact]
    public async Task Repair_LeavesAlreadyCorrectlyTypedCharacter_Untouched()
    {
        var (store, _) = _environment.CreateStoreForClass($"SystemStatsRepair_{Guid.NewGuid():N}");
        const string campaign = "repair-test-healthy-campaign";
        var charId = "chars/repair-test-healthy-npc";

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig
            {
                Id = _keys.Config(campaign),
                ActiveSystem = RulesetSystem.Dnd5e,
            });
            await session.StoreAsync(new Character
            {
                Id = charId,
                Name = "Healthy NPC",
                CampaignName = campaign,
                KeepAlive = true,
                SystemStats = new Dnd5eExtension { ArmorClass = 14 },
            }, charId);
            await session.SaveChangesAsync();
        }

        var repair = new RepairDegradedSystemStats(store);
        var (_, details) = await repair.ExecuteAsync();

        Assert.DoesNotContain(details, d => d.Contains(charId));

        using var readSession = store.OpenAsyncSession();
        var reloaded = await readSession.LoadAsync<Character>(charId);
        var stats = Assert.IsType<Dnd5eExtension>(reloaded!.SystemStats);
        Assert.Equal(14, stats.ArmorClass);
    }

    [Fact]
    public async Task Repair_LeavesNonCombatantCharacter_Untouched()
    {
        var (store, _) = _environment.CreateStoreForClass($"SystemStatsRepair_{Guid.NewGuid():N}");
        const string campaign = "repair-test-noncombatant-campaign";
        var charId = "chars/repair-test-noncombatant-npc";

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new CampaignConfig
            {
                Id = _keys.Config(campaign),
                ActiveSystem = RulesetSystem.Dnd5e,
            });
            await session.StoreAsync(new Character
            {
                Id = charId,
                Name = "Background Extra",
                CampaignName = campaign,
                KeepAlive = false,
                MaxHp = 0,
                SystemStats = new SystemExtension(),
            }, charId);
            await session.SaveChangesAsync();
        }

        var repair = new RepairDegradedSystemStats(store);
        var (_, details) = await repair.ExecuteAsync();

        Assert.DoesNotContain(details, d => d.Contains(charId));
    }
}
