using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Regression guard for the token-bloat fix: before this fix, every entity touched by the ambient
    /// simulation tick (need/memory decay applied campaign-wide on a day-boundary crossing) got unioned
    /// into CommitResult.InvolvedEntities — which feeds the auto-logged SceneCommit event's Involved
    /// list and RefreshInvolvedEntitiesAsync's auto-refresh candidates. On a real campaign with a dozen+
    /// keepAlive NPCs, this meant a single-character rest/travel action could drag the entire campaign
    /// roster (companions, faction NPCs, their locations) into the response as "involved", ballooning
    /// take_turn payloads to 40-50KB and rendering unrelated locations' full scene dossiers. Ambient
    /// drift must still be applied and visible via AmbientDeltas (asserted above) — it just must not be
    /// treated as this turn's narrative involvement.
    /// </summary>
    [Fact]
    public async Task StageChangesAsync_AmbientDeltas_DoNotPolluteInvolvedEntities()
    {
        var slug = NewSlug("ambient-involved");
        var repo = _fixture.CreateRepository(
            engineOverride: new DefaultSimulationEngine(new ISimulationRule[] { new NeedsAccumulationRule() }),
            overrides: b => b.RegisterInstance(new EncounterResolver(() => 1.0)).As<EncounterResolver>());
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var innId = $"locations/{slug}-inn";
        var otherLocId = $"locations/{slug}-far-away";
        var charId = $"chars/{slug}-companion";
        var bystanderId = $"chars/{slug}-bystander";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = innId, Name = "Inn" });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = otherLocId, Name = "Far Away" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = charId,
                Name = "Rest Test Companion",
                IsPartyCompanion = true,
                CurrentLocationId = innId,
                MaxHp = 10,
                CurrentHp = 10
            });
            // A keepAlive NPC unrelated to this commit — the simulation tick still ticks its needs
            // (NeedsAccumulationRule applies to every ScheduledNpc), but it must not surface as
            // "involved" in a commit that never touched it directly.
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = bystanderId,
                Name = "Unrelated Bystander",
                IsPartyCompanion = true,
                CurrentLocationId = otherLocId,
                MaxHp = 10,
                CurrentHp = 10
            });
            await session.SaveChangesAsync();
        }

        using var actSession = _fixture.Store.OpenAsyncSession();
        var actCs = _fixture.CreateCampaignSession(actSession, slug);
        var commitResult = await repo.StageChangesAsync(actCs, new WorldChange[]
        {
            new RestChange { CharacterId = charId, LocationId = innId, IntendedHours = 30, SecurityModifier = 0 }
        });
        await actSession.SaveChangesAsync();

        Assert.True(commitResult.Success, string.Join("; ", commitResult.Summary));
        Assert.Contains(commitResult.AmbientDeltas, d => d is NeedChange nc && nc.CharacterId == bystanderId);
        Assert.Contains(charId, commitResult.InvolvedEntities);
        Assert.DoesNotContain(bystanderId, commitResult.InvolvedEntities);
        Assert.DoesNotContain(otherLocId, commitResult.InvolvedEntities);
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


    [Fact]
    public async Task RefreshInvolvedEntities_ExcludesPcs_FromNpcsList()
    {
        var slug = NewSlug("pcnotnpc");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-hub";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Hub" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // A change that touches only the PC lands the PC id in InvolvedEntities, which
        // RefreshInvolvedEntitiesAsync auto-refreshes by default (AutoRefreshInvolved: true).
        // The PC must never surface in Npcs[] — its state travels via Party/PartyDelta only.
        var afterHpChange = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new HpChange { CharacterId = pcId, Delta = -3 }],
            Narrative = "The PC takes a hit.",
        }, slug);
        Assert.True(afterHpChange.Success, afterHpChange.Summary);
        Assert.DoesNotContain(afterHpChange.Data!.Npcs ?? [], n => n.CharacterId == pcId);

        // Explicitly requesting the PC via ExtraCharacterIds must not force it into Npcs[] either.
        var explicitRequest = await tools.TakeTurn(new TakeTurnRequest
        {
            ExtraCharacterIds = [pcId],
        }, slug);
        Assert.True(explicitRequest.Success, explicitRequest.Summary);
        Assert.DoesNotContain(explicitRequest.Data!.Npcs ?? [], n => n.CharacterId == pcId);
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
    /// Regression guard for the token-bloat fix: FullDetailLocationId populates FullScene via
    /// GetSceneAsync completely independently of RefreshInvolvedEntitiesAsync's Scenes[] pass. When the
    /// requested full-detail location is also this turn's involved location (e.g. a travel
    /// destination), both sections used to render the same location's exits/rumors/recent-events/NPC
    /// roster — once trimmed in Scenes[], once at full detail in FullScene — doubling that location's
    /// contribution to the response for zero informational gain. DedupeScenesCoveredByFullScene should
    /// drop the redundant Scenes[] entry since FullScene is always the richer copy.
    /// </summary>
    [Fact]
    public async Task FullDetailLocationId_DedupesAgainstAutoRefreshedScenes()
    {
        var slug = NewSlug("fullscene-dedupe");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-dest";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            var repo = _fixture.CreateRepository();
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Destination" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new TravelChange { CharacterId = pcId, DestinationLocationId = locId }],
            Narrative = "The PC arrives at the destination.",
            FullDetailLocationId = locId
        }, slug);

        Assert.True(result.Success, result.Summary);
        var data = result.Data!;
        Assert.NotNull(data.FullScene);
        Assert.Equal(locId, data.FullScene!.Location.Id);
        Assert.DoesNotContain(data.Scenes ?? [], s => s.Location.Id.Equals(locId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Regression guard for the RefreshInvolvedEntitiesAsync gear-duplication fix: on mode=delta,
    /// EquippedItems/CarriedItems/SystemStats for a refreshed NPC should only be populated when
    /// something this turn actually touched that NPC's gear/stats (item/character
    /// update/ruleset_action/etc). An untouched NPC's gear was previously re-sent in full on every
    /// single delta turn even though it never changes turn to turn. Requests both ExtraCharacterIds
    /// and ExtraLocationIds for the same NPC/location, so this also covers Finalize's
    /// DedupeNpcsCoveredByScenes: the companion is covered by the refreshed scene, so it must never
    /// appear in Npcs — only in Scenes[].PresentNPCs, which carries the gear-strip verdict there.
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
        Assert.DoesNotContain(seed.Data.Npcs ?? [], n => n.CharacterId == companionId);
        var seedScenePc = Assert.Single(seed.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(seedScenePc.EquippedItems);
        Assert.NotEmpty(seedScenePc.EquippedItems!);

        // Call 2: Delta, nothing touches the companion's gear/stats this turn -> stripped.
        var untouched = await Refresh([new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery, Involved = [companionId] }]);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        Assert.DoesNotContain(untouched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        var untouchedScenePc = Assert.Single(untouched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Null(untouchedScenePc.EquippedItems);
        Assert.Null(untouchedScenePc.CarriedItems);
        Assert.Null(untouchedScenePc.SystemStats);

        // Call 3: Delta, an ItemUnequip touches the companion's gear this turn -> full detail returned.
        var touched = await Refresh([new ItemUnequip { CharacterId = companionId, ItemId = daggerId }]);
        Assert.True(touched.Success, touched.Summary);
        Assert.Equal(TurnMode.Delta, touched.Data!.Mode);
        Assert.DoesNotContain(touched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        var touchedScenePc = Assert.Single(touched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(touchedScenePc.CarriedItems);
        Assert.NotEmpty(touchedScenePc.CarriedItems!);
    }

    /// <summary>
    /// Regression guard: a RulesetAction of a pure-check type (SkillCheck here — same as SavingThrow,
    /// ContestedCheck, OpposedCheck) referencing the acting character must NOT keep gear/stats visible
    /// in delta mode, since neither ruleset resolver ever emits gear/stat mutations for those action
    /// types (unlike Attack/Spell/UseItem/Recovery, which can).
    /// </summary>
    [Fact]
    public async Task DeltaMode_StripsUnchangedGear_WhenOnlyASkillCheckTouchesTheCharacter()
    {
        var slug = NewSlug("gearstrip-check");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-street";
        var companionId = $"chars/{slug}-comp";
        var daggerId = $"items/{slug}-dagger";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Street" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId, Name = "Companion", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                SystemStats = new Dnd5eExtension { ArmorClass = 10, Wisdom = 10 }
            });
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
            Narrative = changes != null ? "The companion checks." : null,
            ExtraCharacterIds = [companionId],
            ExtraLocationIds = [locId]
        }, slug);

        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        var checkOnly = await Refresh([
            new RulesetAction
            {
                CharacterId = companionId,
                ActionName = "Perception",
                ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Perception", ["dc"] = "14" }
            }
        ]);
        Assert.True(checkOnly.Success, checkOnly.Summary);
        Assert.Equal(TurnMode.Delta, checkOnly.Data!.Mode);
        var checkOnlyScenePc = Assert.Single(checkOnly.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Null(checkOnlyScenePc.EquippedItems);
        Assert.Null(checkOnlyScenePc.CarriedItems);
        Assert.Null(checkOnlyScenePc.SystemStats);
    }

    /// <summary>
    /// Companion between the same-scope-but-Npcs-only case: gear-stripping still works correctly on
    /// the Npcs shape when the NPC is requested without also refreshing its location as a scene (so
    /// DedupeNpcsCoveredByScenes has nothing to drop and Npcs is the only surfaced shape).
    /// </summary>
    [Fact]
    public async Task DeltaMode_StripsUnchangedGear_OnNpcsShape_WhenNoSceneRefreshed()
    {
        var slug = NewSlug("gearstrip-npcs");
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
            ExtraCharacterIds = [companionId]
        }, slug);

        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Null(seed.Data!.Scenes);
        var seedNpc = Assert.Single(seed.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(seedNpc.Equipped);
        Assert.NotEmpty(seedNpc.Equipped!);

        var untouched = await Refresh([new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery, Involved = [companionId] }]);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedNpc = Assert.Single(untouched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.Null(untouchedNpc.Equipped);
        Assert.Null(untouchedNpc.Carried);
    }

    /// <summary>
    /// Regression guard for the Npcs/Scenes[].PresentNPCs duplication fix (REFACTOR_STATUS 5.9): an
    /// NPC that is both touched by Changes (lands in InvolvedEntities, auto-selected for initiative
    /// via SelectAndEnrichInitiativeAsync) and located at a scene refreshed in the same call must
    /// appear exactly once, via Scenes[].PresentNPCs, with initiative context intact.
    /// </summary>
    [Fact]
    public async Task TakeTurn_NpcCoveredByRefreshedScene_AppearsOnlyOnceWithInitiativePreserved()
    {
        var slug = NewSlug("dedupe-npc");
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

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "The companion keeps watch.", Category = EventCategory.Discovery, Involved = [companionId] }],
            Narrative = "The party makes camp; the companion stays alert.",
            ExtraLocationIds = [locId]
        }, slug);

        Assert.True(result.Success, result.Summary);
        var data = result.Data!;

        Assert.DoesNotContain(data.Npcs ?? [], n => n.CharacterId == companionId);

        var scene = Assert.Single(data.Scenes!, s => s.Location.Id == locId);
        var presence = Assert.Single(scene.PresentNPCs, n => n.Id == companionId);
        Assert.True(presence.BehavioralTension != 0 || (presence.ActiveInitiatives?.Count ?? 0) > 0 || presence.TurnIntent != null,
            "Expected initiative context to survive dedup via the scene-side NpcPresenceSummary.");
    }

    [Fact]
    public async Task Initiative_CapsAtOne_AndPicksHighestPriorityCandidate()
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
            {
                Id = companionAId, Name = "Companion A", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 90f } }
            });
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

        Assert.DoesNotContain(pcId, withInitiative);
        // Companion A's need stress dominates the priority score (needs+momentum, cheap in-memory
        // estimate — see InitiativeSelectionScorer), so the single slot must go to it.
        Assert.Equal([companionAId], withInitiative);
    }


    [Fact]
    public async Task MemoryHint_IsSuppressed_OnceAlreadySurfacedWithSameTopic()
    {
        var slug = NewSlug("memhint");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-square";
        var pcId = $"chars/{slug}-pc";
        var companionId = $"chars/{slug}-comp";
        var villagerId = $"chars/{slug}-villager";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Square" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            // Companion dominates the initiative priority score (needs stress) so the villager is never
            // the selected winner — it stays in the pool as a non-selected candidate for MemoryHint.
            // Value is deliberately far above the default need baseline (~25) and the initiative
            // cooldown penalty (30, applied to whoever wins the prior call) so it still wins after
            // the Full call below claims the first initiative slot.
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId, Name = "Companion", IsPartyCompanion = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 500f } }
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = villagerId, Name = "Villager", CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10,
                Psychology = new PsychologyProfile
                {
                    Memories = new Dictionary<string, MemoryNode>
                    {
                        ["Secret"] = new MemoryNode
                        {
                            Topic = "Secret",
                            Details = "Saw the mayor meet a stranger at midnight.",
                            Salience = 0.9,
                            Importance = MemoryImportance.Important,
                            DayAcquired = 1
                        }
                    }
                }
            });
            await session.SaveChangesAsync();
        }

        // Call 1 (Full, first-ever call): MemoryHint is only computed in Delta mode, so nothing to
        // assert here beyond getting the cursor initialized.
        var full = await tools.TakeTurn(new TakeTurnRequest
        {
            PartyLocationId = locId,
            ExtraCharacterIds = [villagerId]
        }, slug);
        Assert.True(full.Success, full.Summary);
        Assert.Equal(TurnMode.Full, full.Data!.Mode);

        // Call 2 (Delta): the villager's high-salience memory hasn't been surfaced yet, so it should
        // appear now.
        var firstDelta = await tools.TakeTurn(new TakeTurnRequest
        {
            PartyLocationId = locId,
            ExtraCharacterIds = [villagerId]
        }, slug);
        Assert.True(firstDelta.Success, firstDelta.Summary);
        Assert.Equal(TurnMode.Delta, firstDelta.Data!.Mode);
        var villagerFirst = Assert.Single(firstDelta.Data.Npcs ?? [], n => n.CharacterId == villagerId);
        Assert.NotNull(villagerFirst.MemoryHint);
        Assert.Contains("Secret", villagerFirst.MemoryHint);

        // Call 3 (Delta, nothing changed): same topic already surfaced last call — must be suppressed
        // to avoid repeating the same nudge (and its accompanying querySuggestions entry) every turn.
        var secondDelta = await tools.TakeTurn(new TakeTurnRequest
        {
            PartyLocationId = locId,
            ExtraCharacterIds = [villagerId]
        }, slug);
        Assert.True(secondDelta.Success, secondDelta.Summary);
        Assert.Equal(TurnMode.Delta, secondDelta.Data!.Mode);
        var villagerSecond = Assert.Single(secondDelta.Data.Npcs ?? [], n => n.CharacterId == villagerId);
        Assert.Null(villagerSecond.MemoryHint);
    }

    /// <summary>
    /// Reseed-trigger escalation floor: a relationship shift crossing a +/-40 band on an EARLY delta
    /// turn (TurnsSinceReseed &lt; 3) must NOT escalate, or a single early relationship beat would defeat
    /// delta mode in exactly the long-social-scene case it exists for.
    /// </summary>
    [Fact]
    public async Task ReseedTrigger_RelationshipBand_DoesNotEscalate_BeforeFloor()
    {
        var slug = NewSlug("trigger-floor");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        var npcId = $"chars/{slug}-npc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = npcId, Name = "NPC", MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        // TurnsSinceReseed is 0 here (first delta turn) -> below the floor of 3.
        var escalated = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new RelationshipChange { CharacterId = npcId, TargetId = pcId, Delta = 45, Reason = "Saved my life." }],
            Narrative = "The NPC is overwhelmed with gratitude."
        }, slug);
        Assert.True(escalated.Success, escalated.Summary);
        Assert.Equal(TurnMode.Delta, escalated.Data!.Mode);
    }

    /// <summary>
    /// Once past the escalation floor (TurnsSinceReseed &gt;= 3), a relationship shift crossing a +/-40
    /// band forces the response to Full and resets the reseed counter, same as the periodic path.
    /// </summary>
    [Fact]
    public async Task ReseedTrigger_RelationshipBand_Escalates_AfterFloor()
    {
        var slug = NewSlug("trigger-rel");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        var npcId = $"chars/{slug}-npc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, MaxHp = 10, CurrentHp = 10 });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = npcId, Name = "NPC", MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        // Advance TurnsSinceReseed to 3 with three quiet delta turns (each a pure query refresh, no
        // relationship-affecting changes) before the triggering turn.
        for (var i = 0; i < 3; i++)
        {
            var quiet = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
            Assert.Equal(TurnMode.Delta, quiet.Data!.Mode);
        }

        var escalated = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new RelationshipChange { CharacterId = npcId, TargetId = pcId, Delta = 45, Reason = "Saved my life." }],
            Narrative = "The NPC is overwhelmed with gratitude."
        }, slug);
        Assert.True(escalated.Success, escalated.Summary);
        Assert.Equal(TurnMode.Full, escalated.Data!.Mode);

        // Escalation resets the cursor, so the immediately following turn is Delta again.
        var afterEscalation = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Delta, afterEscalation.Data!.Mode);
    }

    /// <summary>
    /// A PC's location change (ActivityChange.UpdateLocation) is a reseed trigger, same floor rules
    /// as the relationship-band trigger.
    /// </summary>
    [Fact]
    public async Task ReseedTrigger_PcLocationChange_Escalates_AfterFloor()
    {
        var slug = NewSlug("trigger-loc");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        var locAId = $"locations/{slug}-a";
        var locBId = $"locations/{slug}-b";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locAId, Name = "A" });
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locBId, Name = "B" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locAId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        for (var i = 0; i < 3; i++)
        {
            var quiet = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
            Assert.Equal(TurnMode.Delta, quiet.Data!.Mode);
        }

        var escalated = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new ActivityChange { CharacterId = pcId, NewLocationId = locBId, UpdateLocation = true }],
            Narrative = "The party moves to B."
        }, slug);
        Assert.True(escalated.Success, escalated.Summary);
        Assert.Equal(TurnMode.Full, escalated.Data!.Mode);
    }

    /// <summary>
    /// Regression guard: an ActivityChange with UpdateLocation:true but the SAME NewLocationId as the
    /// character's current location (e.g. a POI-only move within one town) must NOT escalate to a full
    /// reseed — only an actual location transition should. Same setup/floor as
    /// ReseedTrigger_PcLocationChange_Escalates_AfterFloor, but the "escalated" call targets locA again.
    /// </summary>
    [Fact]
    public async Task ReseedTrigger_PcSameLocationPoiUpdate_DoesNotEscalate_AfterFloor()
    {
        var slug = NewSlug("trigger-poi");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        var locAId = $"locations/{slug}-a";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locAId, Name = "A" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locAId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        for (var i = 0; i < 3; i++)
        {
            var quiet = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
            Assert.Equal(TurnMode.Delta, quiet.Data!.Mode);
        }

        var stillDelta = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new ActivityChange
                {
                    CharacterId = pcId,
                    NewLocationId = locAId,
                    UpdateLocation = true,
                    NewActivity = "Walking the main street at an ordinary pace"
                }
            ],
            Narrative = "The party walks the streets, staying put in A."
        }, slug);
        Assert.True(stillDelta.Success, stillDelta.Summary);
        Assert.Equal(TurnMode.Delta, stillDelta.Data!.Mode);
    }

    /// <summary>
    /// KnownNeeds in delta mode is filtered to needs that moved >= 2 points this turn; a need that
    /// changed by less than that is omitted. LeanMode further caps the surfaced set at 2 movers.
    /// </summary>
    [Fact]
    public async Task DeltaMode_FiltersKnownNeeds_ToSignificantMoversThisTurn()
    {
        var slug = NewSlug("needsfilter");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Companion", IsPartyCompanion = true, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { ExtraCharacterIds = [companionId] }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        var delta = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes =
            [
                new NeedChange { CharacterId = companionId, Need = "hunger", Delta = 5 },
                new NeedChange { CharacterId = companionId, Need = "boredom", Delta = 1 }
            ],
            Narrative = "The companion grows hungry but stays entertained.",
            ExtraCharacterIds = [companionId]
        }, slug);
        Assert.True(delta.Success, delta.Summary);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);

        var npc = Assert.Single(delta.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.Contains("hunger", npc.KnownNeeds.Keys);
        Assert.DoesNotContain("boredom", npc.KnownNeeds.Keys);
    }

    /// <summary>
    /// CampaignConfig.NeedsChangeSignificanceThreshold governs the KnownNeeds mover cutoff — a campaign
    /// that raises it should stop surfacing deltas the default (2) would have let through.
    /// </summary>
    [Fact]
    public async Task DeltaMode_NeedsSignificanceThreshold_IsConfigurable()
    {
        var slug = NewSlug("needsthreshold");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            var config = await repo.GetCampaignConfigAsync(cs);
            config.NeedsChangeSignificanceThreshold = 10f;
            await repo.UpsertCampaignConfigAsync(session, config, slug);

            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = companionId, Name = "Companion", IsPartyCompanion = true, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { ExtraCharacterIds = [companionId] }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);

        var delta = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new NeedChange { CharacterId = companionId, Need = "hunger", Delta = 5 }],
            Narrative = "The companion grows a little hungry.",
            ExtraCharacterIds = [companionId]
        }, slug);
        Assert.True(delta.Success, delta.Summary);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);

        var npc = Assert.Single(delta.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.DoesNotContain("hunger", npc.KnownNeeds.Keys);
    }

    /// <summary>
    /// Appearance and BehavioralSummary are omitted on delta turns that didn't touch either, and
    /// populated again on a turn that does.
    /// </summary>
    [Fact]
    public async Task DeltaMode_OmitsAppearanceAndBehavioralSummary_WhenUnchanged_ButIncludesWhenTouched()
    {
        var slug = NewSlug("appearance");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId,
                Name = "Companion",
                IsPartyCompanion = true,
                CurrentAppearance = "Travel-worn but cheerful",
                MaxHp = 10,
                CurrentHp = 10
            });
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { ExtraCharacterIds = [companionId] }, slug);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedNpc = Assert.Single(seed.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(seedNpc.CurrentAppearance);
        Assert.NotNull(seedNpc.BehavioralSummary);

        var untouched = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery, Involved = [companionId] }],
            Narrative = "Nothing changes about the companion.",
            ExtraCharacterIds = [companionId]
        }, slug);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedNpc = Assert.Single(untouched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.Null(untouchedNpc.CurrentAppearance);
        Assert.Null(untouchedNpc.BehavioralSummary);

        var touched = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new MoodChange { CharacterId = companionId, NewMood = "elated" }],
            Narrative = "The companion's mood brightens.",
            ExtraCharacterIds = [companionId]
        }, slug);
        Assert.Equal(TurnMode.Delta, touched.Data!.Mode);
        var touchedNpc = Assert.Single(touched.Data.Npcs ?? [], n => n.CharacterId == companionId);
        Assert.NotNull(touchedNpc.BehavioralSummary);
        Assert.Equal("elated", touchedNpc.CurrentMood);
    }

    /// <summary>
    /// RefreshTruncatedIds populate a corresponding QuerySuggestions entry.
    /// </summary>
    [Fact]
    public async Task QuerySuggestions_PopulatedFromRefreshTruncatedIds()
    {
        var slug = NewSlug("querysugg");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var extraIds = Enumerable.Range(0, 7).Select(i => $"chars/{slug}-npc-{i}").ToArray();

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            foreach (var id in extraIds)
            {
                await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
                { Id = id, Name = id, MaxHp = 10, CurrentHp = 10 });
            }
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest { ExtraCharacterIds = extraIds }, slug);
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data!.RefreshTruncatedIds);
        Assert.NotEmpty(result.Data.RefreshTruncatedIds!);
        Assert.NotNull(result.Data.QuerySuggestions);
        Assert.Contains(result.Data.QuerySuggestions!, s => s.Contains(result.Data.RefreshTruncatedIds!.First()));
    }

    /// <summary>
    /// Regression guard: Scenes[].PresentNPCs (built by the shared SceneNpcPresenceFactory, also used
    /// by get_scene) must get the SAME delta-mode leanness as the standalone Npcs[] shape — appearance
    /// and behavioral summary omitted when unchanged this turn, needs filtered to significant movers —
    /// via MutationTools.ApplyDeltaTrim reusing BuildTrim as the single source of truth, applied as a
    /// post-process so the mode-agnostic factory (and get_scene, which has no delta concept) stay untouched.
    /// </summary>
    [Fact]
    public async Task DeltaMode_TrimsScenePresentNpcs_SameAsNpcsShape()
    {
        var slug = NewSlug("scenetrim");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-tavern";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Tavern" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId,
                Name = "Companion",
                IsPartyCompanion = true,
                CurrentLocationId = locId,
                CurrentAppearance = "Travel-worn but cheerful",
                MaxHp = 10,
                CurrentHp = 10
            });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Refresh(WorldChange[]? changes = null) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "The companion acts." : null,
            ExtraLocationIds = [locId]
        }, slug);

        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedPresence = Assert.Single(seed.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(seedPresence.CurrentAppearance);
        Assert.NotNull(seedPresence.BehavioralSummary);

        var untouched = await Refresh([new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery, Involved = [companionId] }]);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedPresence = Assert.Single(untouched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Null(untouchedPresence.CurrentAppearance);
        Assert.Null(untouchedPresence.BehavioralSummary);

        var touched = await Refresh([new MoodChange { CharacterId = companionId, NewMood = "elated" }]);
        Assert.True(touched.Success, touched.Summary);
        Assert.Equal(TurnMode.Delta, touched.Data!.Mode);
        var touchedPresence = Assert.Single(touched.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.NotNull(touchedPresence.BehavioralSummary);
        Assert.Equal("elated", touchedPresence.CurrentMood);
    }

    /// <summary>
    /// WorldStateView.ActiveRumors and Scenes[].LocalRumors are both resolved from the same region-scoped
    /// query when the party's location and the refreshed scene's location share a region — a rumor there
    /// should only be sent once (in ActiveRumors), not duplicated into the scene's LocalRumors too.
    /// </summary>
    [Fact]
    public async Task TakeTurn_DedupesRumorsBetweenWorldStateAndScenes()
    {
        var slug = NewSlug("rumordedup");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-square";
        var rumorId = $"rumors/{slug}-fire";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Town Square" });
            await repo.UpsertRumorAsync(session, new RumorUpsertRequest
            {
                Id = rumorId,
                RegionLocationId = locId,
                Subject = "the fire",
                CurrentText = "They say the granary fire wasn't an accident.",
                State = RumorState.Spreading
            }, slug);
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            PartyLocationId = locId,
            ExtraLocationIds = [locId],
            IncludeWorldState = true
        }, slug);

        Assert.True(result.Success, result.Summary);
        Assert.Contains(result.Data!.WorldState!.ActiveRumors, r => r.Id == rumorId);

        var scene = Assert.Single(result.Data.Scenes!, s => s.Location.Id == locId);
        Assert.DoesNotContain(scene.LocalRumors, r => r.Id == rumorId);
    }

    /// <summary>
    /// Regression guard: scene.LocalRumors must get the same delta-mode leanness as everything else —
    /// only rumors this turn's changes actually evolved/created should be sent; untouched rumors are
    /// already known to the client from the last full reseed. Runs with IncludeWorldState=false so
    /// DedupeRumorsCoveredByWorldState (a separate mechanism, covered by the test above) can't mask this.
    /// </summary>
    [Fact]
    public async Task DeltaMode_StripsUnchangedLocalRumors_ButKeepsOnesTouchedThisTurn()
    {
        var slug = NewSlug("rumortrim");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-square";
        var quietRumorId = $"rumors/{slug}-quiet";
        var evolvingRumorId = $"rumors/{slug}-fire";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Town Square" });
            await repo.UpsertRumorAsync(session, new RumorUpsertRequest
            {
                Id = quietRumorId,
                RegionLocationId = locId,
                Subject = "the well",
                CurrentText = "The well water tastes odd lately.",
                State = RumorState.Spreading
            }, slug);
            await repo.UpsertRumorAsync(session, new RumorUpsertRequest
            {
                Id = evolvingRumorId,
                RegionLocationId = locId,
                Subject = "the fire",
                CurrentText = "They say the granary fire wasn't an accident.",
                State = RumorState.Nascent
            }, slug);
            await session.SaveChangesAsync();
        }

        var seed = await tools.TakeTurn(new TakeTurnRequest { ExtraLocationIds = [locId] }, slug);
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedScene = Assert.Single(seed.Data.Scenes!, s => s.Location.Id == locId);
        Assert.Contains(seedScene.LocalRumors, r => r.Id == quietRumorId);
        Assert.Contains(seedScene.LocalRumors, r => r.Id == evolvingRumorId);

        var untouched = await tools.TakeTurn(new TakeTurnRequest
        {
            ExtraLocationIds = [locId],
            Changes = [new EventOccurred { Summary = "Idle chatter.", Category = EventCategory.Discovery }],
            Narrative = "Nothing rumor-related happens."
        }, slug);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedScene = Assert.Single(untouched.Data.Scenes!, s => s.Location.Id == locId);
        Assert.Empty(untouchedScene.LocalRumors);

        var evolved = await tools.TakeTurn(new TakeTurnRequest
        {
            ExtraLocationIds = [locId],
            Changes = [new RumorEvolves { RumorId = evolvingRumorId, NewState = RumorState.Spreading }],
            Narrative = "Word of the fire spreads."
        }, slug);
        Assert.True(evolved.Success, evolved.Summary);
        Assert.Equal(TurnMode.Delta, evolved.Data!.Mode);
        var evolvedScene = Assert.Single(evolved.Data.Scenes!, s => s.Location.Id == locId);
        Assert.DoesNotContain(evolvedScene.LocalRumors, r => r.Id == quietRumorId);
        Assert.Contains(evolvedScene.LocalRumors, r => r.Id == evolvingRumorId);
    }

    [Fact]
    public async Task PartyFingerprint_IsEchoedAndStableAcrossQueryOnlyCalls()
    {
        var slug = NewSlug("fingerprint-stable");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-hub";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Hub" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var first = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.True(first.Success, first.Summary);
        Assert.False(string.IsNullOrEmpty(first.Data!.PartyFingerprint));
        Assert.Contains(pcId, first.Data.PartyFingerprint);

        // Correctly echoing the previous fingerprint with no party changes in between should never
        // itself trigger a forced full reseed or a drift advisory.
        var second = await tools.TakeTurn(new TakeTurnRequest
        {
            IncludeWorldState = true,
            ClientPartyFingerprint = first.Data.PartyFingerprint
        }, slug);
        Assert.True(second.Success, second.Summary);
        Assert.Equal(TurnMode.Delta, second.Data!.Mode);
        Assert.Equal(first.Data.PartyFingerprint, second.Data.PartyFingerprint);
        Assert.Null(second.Data.NarrativeReminder);
    }

    [Fact]
    public async Task PartyFingerprint_MismatchForcesFullReseedWithAdvisory()
    {
        var slug = NewSlug("fingerprint-drift");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-hub";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Hub" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var first = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.True(first.Success, first.Summary);

        var second = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Delta, second.Data!.Mode);

        // Simulate a client that missed a delta: echo a stale/bogus fingerprint on the next call.
        var driftTurn = await tools.TakeTurn(new TakeTurnRequest
        {
            IncludeWorldState = true,
            ClientPartyFingerprint = "chars/does-not-exist:1/1@nowhere"
        }, slug);
        Assert.True(driftTurn.Success, driftTurn.Summary);
        Assert.Equal(TurnMode.Full, driftTurn.Data!.Mode);
        Assert.NotNull(driftTurn.Data.NarrativeReminder);
        Assert.Contains("clientPartyFingerprint", driftTurn.Data.NarrativeReminder);

        // Drift-forced reseed resets the cadence like any other full reseed.
        var afterDrift = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(TurnMode.Delta, afterDrift.Data!.Mode);
    }

    [Fact]
    public async Task WorldSequence_IncrementsOnlyOnCommittedMutations()
    {
        var slug = NewSlug("worldseq");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-hub";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Hub" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        var pureQuery = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.True(pureQuery.Success, pureQuery.Summary);
        Assert.Equal(0, pureQuery.Data!.WorldSequence);

        var mutation = await tools.TakeTurn(new TakeTurnRequest
        {
            Changes = [new NeedChange { CharacterId = pcId, Need = "hunger", Delta = 5 }],
            Narrative = "PC grows hungry.",
            IncludeWorldState = true
        }, slug);
        Assert.True(mutation.Success, mutation.Summary);
        Assert.Equal(1, mutation.Data!.WorldSequence);

        var anotherPureQuery = await tools.TakeTurn(new TakeTurnRequest { IncludeWorldState = true }, slug);
        Assert.Equal(1, anotherPureQuery.Data!.WorldSequence);
    }

    /// <summary>
    /// Regression guard for the NeedDescriptors token-budget fix: the reference-text descriptor
    /// dictionary on Scenes[].PresentNPCs (only shape that carries it — NpcSummaryView/Npcs[] has no
    /// NeedDescriptors field at all) must get the SAME delta-mode significant-movers filter as
    /// KnownNeeds, via MutationTools.ApplyDeltaTrim, instead of always re-sending the full merged
    /// (global + per-NPC) descriptor set on every turn regardless of what actually moved.
    /// </summary>
    [Fact]
    public async Task DeltaMode_FiltersNeedDescriptors_SameAsKnownNeeds()
    {
        var slug = NewSlug("descriptorfilter");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-tavern";
        var companionId = $"chars/{slug}-comp";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = locId, Name = "Tavern" });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            {
                Id = companionId,
                Name = "Companion",
                IsPartyCompanion = true,
                CurrentLocationId = locId,
                MaxHp = 10,
                CurrentHp = 10,
                Needs = new NeedsProfile
                {
                    ActiveNeeds = new Dictionary<string, float> { ["hunger"] = 10f, ["boredom"] = 10f },
                    NeedDescriptors = new Dictionary<string, string>
                    {
                        ["hunger"] = "Custom hunger descriptor text.",
                        ["boredom"] = "Custom boredom descriptor text."
                    }
                }
            });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Refresh(WorldChange[]? changes = null) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "The companion reacts." : null,
            ExtraLocationIds = [locId]
        }, slug);

        // Full (first-ever call): both descriptors present.
        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedPresence = Assert.Single(seed.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Contains("hunger", seedPresence.NeedDescriptors.Keys);
        Assert.Contains("boredom", seedPresence.NeedDescriptors.Keys);

        // Delta: only hunger moved >= the significance threshold this turn -> only hunger's descriptor
        // should ride along, same filter KnownNeeds already gets.
        var delta = await Refresh(
        [
            new NeedChange { CharacterId = companionId, Need = "hunger", Delta = 5 },
            new NeedChange { CharacterId = companionId, Need = "boredom", Delta = 1 }
        ]);
        Assert.True(delta.Success, delta.Summary);
        Assert.Equal(TurnMode.Delta, delta.Data!.Mode);
        var deltaPresence = Assert.Single(delta.Data.Scenes!.Single(s => s.Location.Id == locId).PresentNPCs, n => n.Id == companionId);
        Assert.Contains("hunger", deltaPresence.KnownNeeds.Keys);
        Assert.DoesNotContain("boredom", deltaPresence.KnownNeeds.Keys);
        Assert.Contains("hunger", deltaPresence.NeedDescriptors.Keys);
        Assert.DoesNotContain("boredom", deltaPresence.NeedDescriptors.Keys);
    }

    /// <summary>
    /// Regression guard for the Scenes[].Location token-budget fix: unlike PresentNPCs (already
    /// delta-trimmed via ApplyDeltaTrim), the Location wrapper itself used to resend its full
    /// description/exits/POIs/tags/metadata every single scene refresh regardless of mode — pure
    /// waste on a multi-round combat/social turn sequence that keeps refreshing the same room. On an
    /// untouched delta turn it should collapse to id/name/type/parent/danger/faction only; a
    /// LocationUpdate targeting it, or a character's ActivityChange/TravelChange arrival into it,
    /// restores full detail for that turn.
    /// </summary>
    [Fact]
    public async Task DeltaMode_StripsUnchangedLocationDetail_ButRestoresOnUpdateOrArrival()
    {
        var slug = NewSlug("locationtrim");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var locId = $"locations/{slug}-shrine";
        var pcId = $"chars/{slug}-pc";

        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertLocationAsync(cs, new LocationUpsertRequest
            {
                Id = locId,
                Name = "Collapsed Shrine",
                Description = "Rubble-strewn floor, one wall open to the night sky.",
                Type = LocationType.Building
            });
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
            { Id = pcId, Name = "PC", IsPc = true, CurrentLocationId = locId, MaxHp = 10, CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        Task<ToolResult<TurnResult>> Refresh(WorldChange[]? changes = null) => tools.TakeTurn(new TakeTurnRequest
        {
            Changes = changes,
            Narrative = changes != null ? "Something happens." : null,
            ExtraLocationIds = [locId]
        }, slug);

        // Full (first-ever call): full location detail.
        var seed = await Refresh();
        Assert.True(seed.Success, seed.Summary);
        Assert.Equal(TurnMode.Full, seed.Data!.Mode);
        var seedLoc = Assert.Single(seed.Data.Scenes!).Location;
        Assert.Equal("Rubble-strewn floor, one wall open to the night sky.", seedLoc.Description);

        // Delta, nothing touches this location this turn (an unrelated HP change on the PC) -> Location
        // collapses to its identity fields; id/name survive so the client can still match it up.
        var untouched = await Refresh([new HpChange { CharacterId = pcId, Delta = -3 }]);
        Assert.True(untouched.Success, untouched.Summary);
        Assert.Equal(TurnMode.Delta, untouched.Data!.Mode);
        var untouchedLoc = Assert.Single(untouched.Data.Scenes!).Location;
        Assert.Equal(locId, untouchedLoc.Id);
        Assert.Equal("Collapsed Shrine", untouchedLoc.Name);
        Assert.Equal("", untouchedLoc.Description);
        Assert.Empty(untouchedLoc.Exits);

        // Delta, a LocationUpdate targets this location this turn -> full detail restored.
        var updated = await Refresh([new LocationUpdate { LocationId = locId, NewState = "A section of the roof has caved in." }]);
        Assert.True(updated.Success, updated.Summary);
        Assert.Equal(TurnMode.Delta, updated.Data!.Mode);
        var updatedLoc = Assert.Single(updated.Data.Scenes!).Location;
        Assert.Equal("Rubble-strewn floor, one wall open to the night sky.", updatedLoc.Description);
        Assert.Equal("A section of the roof has caved in.", updatedLoc.CurrentState);
    }

    [Fact]
    public async Task PhysicalStateReminder_FiresWhenFlaggedEventHasNoMatchingCommit()
    {
        var slug = NewSlug("physical-drift");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var repo = _fixture.CreateRepository();
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = pcId, Name = "PC", IsPc = true });
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Narrative = "The blade flashes and cuts the ropes at her wrists.",
            Changes =
            [
                new EventOccurred
                {
                    Summary = "Freed the captive.", Category = EventCategory.Discovery, Involved = [pcId],
                    ImpliesPersistentPhysicalChange = true
                }
            ]
        }, slug);

        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data!.NarrativeReminder);
        Assert.Contains("impliesPersistentPhysicalChange", result.Data.NarrativeReminder);
    }

    [Fact]
    public async Task PhysicalStateReminder_StaysSilentWhenMatchingCommitPresent()
    {
        var slug = NewSlug("physical-nodrift");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);

        var pcId = $"chars/{slug}-pc";
        using (var session = _fixture.Store.OpenAsyncSession())
        {
            var repo = _fixture.CreateRepository();
            var cs = _fixture.CreateCampaignSession(session, slug);
            await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest { Id = pcId, Name = "PC", IsPc = true });
            await session.SaveChangesAsync();
        }

        var result = await tools.TakeTurn(new TakeTurnRequest
        {
            Narrative = "The blade flashes and cuts the ropes at her wrists.",
            Changes =
            [
                new EventOccurred
                {
                    Summary = "Freed the captive.", Category = EventCategory.Discovery, Involved = [pcId],
                    ImpliesPersistentPhysicalChange = true
                },
                new StatusRemove { CharacterId = pcId, Status = "Restrained" }
            ]
        }, slug);

        Assert.True(result.Success, result.Summary);
        Assert.Null(result.Data!.NarrativeReminder);
    }
}
