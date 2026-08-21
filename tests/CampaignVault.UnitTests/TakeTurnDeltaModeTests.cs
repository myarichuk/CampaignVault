using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Coverage for take_turn's Full/Delta reseed cadence (TurnCursor), the ambient-simulation-drift
/// surfacing fix in CampaignRepository.StageChangesAsync, delta-mode PartyDelta/WorldStateDelta
/// content, and the capped NPC initiative/memory selection.
/// </summary>
[Collection("RavenDB")]
public class TakeTurnDeltaModeTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public TakeTurnDeltaModeTests(RavenDBFixture fixture) => _fixture = fixture;

    private static string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Mode_AlternatesFullThenDelta_AndForcedReseedResetsCounter()
    {
        var slug = NewSlug("cursor");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            var config = await repo.GetCampaignConfigAsync(cs);
            config.DeltaModeReseedIntervalTurns = 2;
            await repo.UpsertCampaignConfigAsync(session, config, slug);
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Query(bool forceFullReseed = false) =>
            tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true, ForceFullReseed = forceFullReseed }, slug);

        var first = await Query();
        Assert.True(first.Success, first.Summary);
        Assert.Equal(TurnMode.Full, first.Data!.Mode);
        Assert.NotNull(first.Data.WorldState);
        Assert.Null(first.Data.WorldStateDelta);

        var second = await Query();
        Assert.Equal(TurnMode.Delta, second.Data!.Mode);
        Assert.Null(second.Data.WorldState);
        Assert.NotNull(second.Data.WorldStateDelta);

        var third = await Query();
        Assert.Equal(TurnMode.Delta, third.Data!.Mode);

        // Interval is 2: calls 2 and 3 are the two delta turns, call 4 crosses the threshold -> Full.
        var fourth = await Query();
        Assert.Equal(TurnMode.Full, fourth.Data!.Mode);

        var fifth = await Query();
        Assert.Equal(TurnMode.Delta, fifth.Data!.Mode);

        // forceFullReseed overrides the natural cycle and resets the counter.
        var forced = await Query(forceFullReseed: true);
        Assert.Equal(TurnMode.Full, forced.Data!.Mode);

        var afterForced = await Query();
        Assert.Equal(TurnMode.Delta, afterForced.Data!.Mode);
    }

    [Fact]
    public async Task AdvanceWorld_ForcesNextTakeTurnToFull()
    {
        var slug = NewSlug("advance-reseed");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        var delta = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);

        var advance = await tools.AdvanceWorld(days: 1, resultingHour: 8, narrative: "Time passes.", campaignName: slug);
        Assert.True(advance.Success, advance.Summary);

        var afterAdvance = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, afterAdvance.Data!.Mode);
    }

    /// <summary>
    /// Regression guard for the CampaignRepository.StageChangesAsync fix (Phase 0): before the fix,
    /// RunSimulationTickAsync's return value was discarded, so ambient drift (needs/memory decay
    /// triggered by a day-boundary crossing) was persisted but invisible to the caller. A rigged
    /// EncounterResolver (no interruption) and a minimal single-rule simulation engine
    /// (NeedsAccumulationRule, which unconditionally emits a NeedChange for any scheduled NPC when
    /// days &gt; 0) make this fully deterministic — no flakiness from encounter-interruption RNG.
    /// </summary>
    [Fact]
    public async Task StageChangesAsync_SurfacesAmbientSimulationDeltas_WhenDayBoundaryCrossed()
    {
        var slug = NewSlug("ambient");
        var repo = _fixture.CreateRepository(
            engineOverride: new DefaultSimulationEngine(new ISimulationRule[] { new NeedsAccumulationRule() }),
            overrides: b => b.RegisterInstance(new EncounterResolver(() => 1.0)).As<EncounterResolver>());
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-inn";
        var charId = $"chars/{slug}-companion";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Inn" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Rest Test Companion",
                IsPartyCompanion = true,
                CurrentLocationId = locId,
                MaxHp = 10,
                CurrentHp = 10
            });
            await session.SaveChangesAsync();
        }

        using var actSession = _fixture.Store.OpenAsyncSession();
        var actCs = _fixture.CreateCampaignSession(actSession, slug);
        var commitResult = await repo.StageChangesAsync(actCs, new WorldChange[]
        {
            new RestChange { CharacterId = charId, LocationId = locId, IntendedHours = 30, SecurityModifier = 0 }
        });
        await actSession.SaveChangesAsync();

        Assert.True(commitResult.Success, string.Join("; ", commitResult.Summary));
        Assert.NotEmpty(commitResult.AmbientDeltas);
        Assert.Contains(commitResult.AmbientDeltas, d => d is NeedChange nc && nc.CharacterId == charId);
    }

    [Fact]
    public async Task DeltaMode_PartyAndWorldState_OnlyReflectThisTurnsChanges()
    {
        var slug = NewSlug("deltacontent");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-hub";
        var pcId = $"chars/{slug}-pc";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Hub" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Companion", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // Call 1 (Full, first-ever call): confirms the sibling-field parity invariant — full sections
        // populated, delta sections null — before any delta-mode behavior kicks in.
        var full = await tools.TakeTurn(new TakeTurnRequest
        {
            IncludeParty = true,
            IncludeWorldState = true,
            PartyLocationId = locId
        }, slug);
        Assert.True(full.Success, full.Summary);
        Assert.Equal(TurnMode.Full, full.Data!.Mode);
        Assert.NotNull(full.Data.Party);
        Assert.NotNull(full.Data.WorldState);
        Assert.Null(full.Data.PartyDelta);
        Assert.Null(full.Data.WorldStateDelta);

        // Call 2 (Delta): commit a NeedChange against the companion only.
        var delta = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new NeedChange { CharacterId = companionId, Need = "hunger", Delta = 5 }],
            Narrative = "The companion grows hungry.",
            IncludeParty = true,
            IncludeWorldState = true,
            PartyLocationId = locId
        }, slug);
        Assert.True(delta.Success, delta.Summary);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);
        Assert.Null(delta.Data.Party);
        Assert.Null(delta.Data.WorldState);
        Assert.NotNull(delta.Data.PartyDelta);
        Assert.NotNull(delta.Data.WorldStateDelta);
        Assert.NotNull(delta.Data.WorldStateDelta!.Time);

        var companionDelta = Assert.Single(delta.Data.PartyDelta!, d => d.EntityId == companionId);
        Assert.Contains(companionDelta.Changes, c => c is NeedChange nc && nc.Need == "hunger");

        // The untouched PC has no changes and is never initiative-eligible (PCs are excluded), so it
        // should not appear in the delta at all.
        Assert.DoesNotContain(delta.Data.PartyDelta!, d => d.EntityId == pcId);

        // Delta payload should be meaningfully smaller than the equivalent full payload for the same fixture.
        var fullJson = JsonSerializer.Serialize(full.Data);
        var deltaJson = JsonSerializer.Serialize(delta.Data);
        Assert.True(deltaJson.Length < fullJson.Length,
            $"Expected delta payload ({deltaJson.Length} chars) to be smaller than full payload ({fullJson.Length} chars).");
    }

    /// <summary>
    /// Regression guard: NpcInitiativeService.Enrich has a persisted side effect (it marks surfaced
    /// initiative candidates as consumed on the campaign doc), so an enrichment that's computed but
    /// never attached anywhere in the response silently burns candidates for nothing — worse than not
    /// enriching at all. A bare take_turn (no includeParty, no extraCharacterIds) previously discarded
    /// the enrichment entirely since Npcs/Party never included the companion. EnsureInitiativeSurfacedAsync
    /// fixes this by appending a lightweight NpcSummaryView when a selected NPC isn't already covered.
    /// </summary>
    [Fact]
    public async Task PlainTakeTurn_SurfacesInitiative_WithoutIncludePartyOrExtraIds()
    {
        var slug = NewSlug("bare-initiative");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-camp";
        var pcId = $"chars/{slug}-pc";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Camp" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Companion", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // No includeParty, no extraCharacterIds, no partyLocationId — the initiative pool must fall back
        // to a PC's CurrentLocationId to find the companion, and EnsureInitiativeSurfacedAsync must append
        // it to Npcs since it wouldn't otherwise appear anywhere in this response shape.
        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "The party makes camp.", Category = EventCategory.Discovery }],
            Narrative = "The party settles in for the evening."
        }, slug);

        Assert.True(result.Success, result.Summary);
        var data = result.Data!;
        Assert.Null(data.Party);
        Assert.Null(data.PartyDelta);

        var companionSummary = Assert.Single(data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(companionSummary.Initiative);
    }

    /// <summary>
    /// Regression guard for the RefreshInvolvedEntitiesAsync gear-duplication fix: on mode=delta,
    /// EquippedItems/CarriedItems/SystemStats for a refreshed NPC — whether surfaced via top-level
    /// Npcs (ExtraCharacterIds) or nested in Scenes[].PresentNPCs (ExtraLocationIds) — should only be
    /// populated when something this turn actually touched that NPC's gear/stats (item/character
    /// update/ruleset_action/etc). An untouched NPC's gear was previously re-sent in full on every
    /// single delta turn even though it never changes turn to turn.
    /// </summary>
    [Fact]
    public async Task DeltaMode_StripsUnchangedGear_ButKeepsItWhenTouchedThisTurn()
    {
        var slug = NewSlug("gearstrip");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-tavern";
        var companionId = $"chars/{slug}-comp";
        var daggerId = $"items/{slug}-dagger";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Tavern" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Companion", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertItemAsync(cs, new ItemUpsertRequest
            {
                Id = daggerId,
                Name = "Dagger",
                Description = "A plain dagger.",
                HolderId = companionId,
                CoreCategory = ItemCategory.Weapon,
                EquipZones = [EquipZone.Accessory],
                EquipLayer = EquipLayer.Held,
                IsEquipped = true
            });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Refresh(WorldChange[]? changes = null) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "The companion acts." : null,
            ExtraCharacterIds = [companionId],
            ExtraLocationIds = [locId]
        }, slug);

        // Call 1: Full (first-ever call) — gear always populated regardless of mode.
        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedNpc = Assert.Single(seed.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(seedNpc.Equipped);
        Assert.NotEmpty(seedNpc.Equipped!);
        var seedScenePc = Assert.Single(seed.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(seedScenePc.EquippedItems);
        Assert.NotEmpty(seedScenePc.EquippedItems!);

        // Call 2: Delta, nothing touches the companion's gear/stats this turn -> stripped in both shapes.
        var untouched = await Refresh([new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery, Involved = [companionId] }]);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedNpc = Assert.Single(untouched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.Null(untouchedNpc.Equipped);
        Assert.Null(untouchedNpc.Carried);
        var untouchedScenePc = Assert.Single(untouched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Null(untouchedScenePc.EquippedItems);
        Assert.Null(untouchedScenePc.CarriedItems);
        Assert.Null(untouchedScenePc.SystemStats);

        // Call 3: Delta, an ItemUnequip touches the companion's gear this turn -> full detail returned.
        var touched = await Refresh([new ItemUnequip { CharacterId = companionId, ItemId = daggerId }]);
        Assert.True(touched.Success, touched.Summary);
        Assert.Equal(TurnMode.Delta, touched.Data!.Mode);
        var touchedNpc = Assert.Single(touched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(touchedNpc.Carried);
        Assert.NotEmpty(touchedNpc.Carried!);
        var touchedScenePc = Assert.Single(touched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(touchedScenePc.CarriedItems);
        Assert.NotEmpty(touchedScenePc.CarriedItems!);
    }

    [Fact]
    public async Task Initiative_CapsAtTwo_AndIncludesOneCompanionPlusOneOther()
    {
        var slug = NewSlug("initiative");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-square";
        var pcId = $"chars/{slug}-pc";
        var companionAId = $"chars/{slug}-comp-a";
        var companionBId = $"chars/{slug}-comp-b";
        var villagerId = $"chars/{slug}-villager";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Square" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionAId, Name = "Companion A", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionBId, Name = "Companion B", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = villagerId, Name = "Villager", CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            IncludeParty = true,
            PartyLocationId = locId,
            ExtraCharacterIds = [villagerId]
        }, slug);

        Assert.True(result.Success, result.Summary);
        var data = result.Data!;

        var withInitiative = (data.Party ?? [])
            .Where(p => p.Initiative != null)
            .Select(p => p.Id)
            .Concat((data.PartyDelta ?? []).Where(d => d.Initiative != null).Select(d => d.EntityId))
            .Concat((data.Npcs ?? []).Where(n => n.Initiative != null).Select(n => n.CharacterId))
            .Distinct()
            .ToList();

        Assert.True(withInitiative.Count <= 2,
            $"Expected at most 2 NPCs with initiative, got {withInitiative.Count}: {string.Join(",", withInitiative)}");
        Assert.Contains(withInitiative, id => id == companionAId || id == companionBId);
        Assert.DoesNotContain(pcId, withInitiative);
        // With 2 companions + 1 non-companion NPC present, the fill slot should prefer the non-companion.
        Assert.Contains(villagerId, withInitiative);
        Assert.Equal(2, withInitiative.Count);
    }
}
