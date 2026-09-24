using CampaignVault.Data;
using CampaignVault.Data.Context;
using CampaignVault.Models;

namespace CampaignVault.Tools;

/// <summary>
/// T6 need-driven responses: each take_turn answers "what does the model need for this beat?" and the
/// TurnCursor ledger keeps anything it already has from being re-sent. Runs after every section is
/// built, so it only reshapes what the earlier steps assembled:
/// <list type="bullet">
/// <item>scene rosters: every present NPC stays listed (id, name, activity, mood, a short note), the rest moves to cards;</item>
/// <item>cards: once per session, on arrival for spotlight NPCs and on the first commit involving anyone else;</item>
/// <item>npcs[] echo: only involved NPCs whose mood/activity/looks/gear changed, or who are about to act;</item>
/// <item>context lines from the IContextContributors (memories the topic brings up, tier crossings, gold, ...);</item>
/// <item>a location's description and recent events once per session (first visit), not on every revisit.</item>
/// </list>
/// </summary>
public partial class MutationTools
{
    /// <summary>Roster notes are a hook, not the NPC's file: the card or get_entity has the rest.</summary>
    private const int RosterNoteCap = 80;

    private const double CardMemoryMatchThreshold = 0.35;

    private async Task BriefAsync(TurnContext ctx)
    {
        try
        {
            if (ctx.Mode == TurnMode.Full)
            {
                // The client may have lost everything (new conversation, compaction, drift): start over.
                ctx.Cursor.ClearDeliveryLedger();
            }

            var party = await ctx.Session.Query<Character>()
                .Where(c => c.CampaignName == ctx.Campaign && (c.IsPc || c.IsPartyCompanion))
                .ToListAsync();
            var partyIds = new HashSet<string>(party.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

            var involvedNpcIds = ctx.InvolvedEntityIds
                .Where(id => id.StartsWith(CanonicalId.Characters, StringComparison.OrdinalIgnoreCase) && !partyIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A delta-refetched scene row is already trimmed to what changed this turn (ApplyDeltaTrim):
            // significant need movers, touched gear/looks. That stays. A full scene (arrival, reseed) is
            // untrimmed, and that is where the roster cut applies.
            var deltaRows = ctx.Mode == TurnMode.Delta;
            var presentScenes = (ctx.Result.Scenes ?? []).Select(s => (Trimmed: deltaRows, Get: (Func<IEnumerable<NpcPresenceSummary>>)(() => s.PresentNPCs), Set: (Action<IEnumerable<NpcPresenceSummary>>)(v => s.PresentNPCs = v)))
                .ToList();
            if (ctx.Result.FullScene is { } full)
            {
                presentScenes.Add((false, () => full.PresentNPCs, v => full.PresentNPCs = v));
            }

            var presentNpcs = presentScenes
                .SelectMany(s => s.Get())
                .Where(n => !n.IsPc)
                .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var spotlight = await SpotlightIdsAsync(ctx, presentNpcs, partyIds);
            // Companions ride in party sections only with includeParty, so they are always in the spotlight.
            var cardIds = presentNpcs
                .Where(n => n.IsPartyCompanion || spotlight.Contains(n.Id))
                .Select(n => n.Id)
                .Concat(involvedNpcIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var cards = await BuildUndeliveredCardsAsync(ctx, cardIds, party);
            if (cards.Count > 0)
            {
                ctx.Result.Cards = cards;
            }

            var cardedIds = new HashSet<string>(cards.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var scene in presentScenes)
            {
                var trimmed = scene.Trimmed;
                scene.Set(scene.Get().Select(n => n.IsPc ? n : RosterEntry(n,
                    // The hook note stands in for a card not yet sent; a card in this response carries the notes.
                    withNote: !ctx.Cursor.DeliveredCardHashes.ContainsKey(n.Id),
                    keepChanges: trimmed && !cardedIds.Contains(n.Id))).ToList());
            }

            FilterNpcEcho(ctx, partyIds, cardedIds);
            await BriefLocationsAsync(ctx);
            DropEmptyDeltaScenes(ctx, party);

            if (ctx.AppliedChanges.Count > 0)
            {
                var memoryLinesInCards = new HashSet<string>(cards.SelectMany(c => c.Memories ?? []));
                var turn = new ContextTurn
                {
                    Session = ctx.Session,
                    CampaignName = ctx.Campaign,
                    Config = ctx.Config,
                    AppliedChanges = ctx.AppliedChanges,
                    InvolvedEntityIds = ctx.InvolvedEntityIds,
                    Party = party,
                    PartyLocationId = party.FirstOrDefault(p => p.IsPc)?.CurrentLocationId ?? party.FirstOrDefault()?.CurrentLocationId,
                    PresentNpcIds = presentNpcs.Select(n => n.Id).ToList(),
                    NarrativeVector = ctx.NarrativeVector,
                    RelationshipBaselines = ctx.RelationshipBaselines,
                    MemoryLinesInCards = memoryLinesInCards
                };
                var lines = await _contextOrchestrator.CollectAsync(turn, ctx.Cursor.DeliveredContextKeys);
                if (lines.Count > 0)
                {
                    ctx.Result.Context = lines.ToList();
                }
            }
        }
        catch (Exception ex)
        {
            Warn(ctx, $"Turn briefing failed: {ex.Message}", ex);
        }
    }

    /// <summary>Who deserves a card on sight: plot- or quest-linked, about to act, keepAlive, or someone
    /// with an opinion of the party. Everyone else gets a card when the party first engages them.</summary>
    private async Task<HashSet<string>> SpotlightIdsAsync(TurnContext ctx, IReadOnlyList<NpcPresenceSummary> present, HashSet<string> partyIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0)
        {
            return ids;
        }

        var presentIds = new HashSet<string>(present.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var npc in present)
        {
            if (npc.KeepAlive || npc.TurnIntent != null || npc.ActiveInitiatives is { Count: > 0 })
            {
                ids.Add(npc.Id);
            }
        }

        var characters = await ctx.Session.LoadAsync<Character>(presentIds);
        foreach (var c in characters.Values)
        {
            if (c?.Social?.Relationships?.Any(r => partyIds.Contains(r.Key) && r.Value != 0) == true)
            {
                ids.Add(c.Id);
            }
        }

        var threads = await _repository.GetActivePlotThreadsAsync(ctx.Session, ctx.Campaign);
        foreach (var id in threads.SelectMany(t => t.InvolvedEntityIds).Where(presentIds.Contains))
        {
            ids.Add(id);
        }

        var quests = await ctx.Session.Query<Quest>()
            .Where(q => q.CampaignName == ctx.Campaign && q.OverallState == QuestState.Open && !q.IsArchived)
            .Take(50)
            .ToListAsync();
        foreach (var quest in quests)
        {
            if (quest.GiverId != null && presentIds.Contains(quest.GiverId))
            {
                ids.Add(quest.GiverId);
            }

            foreach (var id in quest.Objectives.Where(o => o.State == QuestState.Open)
                         .SelectMany(o => o.InvolvedIds ?? []).Where(presentIds.Contains))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<List<NpcCard>> BuildUndeliveredCardsAsync(TurnContext ctx, IReadOnlyList<string> ids, IReadOnlyList<Character> party)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var characters = await ctx.Session.LoadAsync<Character>(ids);
        var npcs = characters.Values.Where(c => c is { IsPc: false }).Select(c => c!).ToList();
        if (npcs.Count == 0)
        {
            return [];
        }

        var npcIds = npcs.Select(n => n.Id).ToList();
        var held = await ctx.Session.Query<Item>()
            // `!= true`, not `!Hidden`: Raven skips documents saved before the field existed for `Hidden == false`.
            .Where(i => i.HolderId.In(npcIds) && !i.IsArchived && i.Hidden != true)
            .ToListAsync();

        var cards = new List<NpcCard>();
        foreach (var npc in npcs)
        {
            var card = NpcCardFactory.Build(npc, party,
                held.Where(i => i.HolderId.Equals(npc.Id, StringComparison.OrdinalIgnoreCase)).ToList(),
                MemoriesForThisBeat(ctx, npc));
            var hash = card.StableHash();
            if (ctx.Cursor.DeliveredCardHashes.TryGetValue(npc.Id, out var sent) && sent == hash)
            {
                continue;
            }

            ctx.Cursor.DeliveredCardHashes[npc.Id] = hash;
            if (card with { Mood = null } != new NpcCard(card.Id, card.Name))
            {
                cards.Add(card); // A card with nothing beyond id and name tells the model nothing.
            }
        }

        return cards;
    }

    /// <summary>A card's two memories are the ones this beat is about when the narrative has an embedding;
    /// otherwise the NPC's most important ones.</summary>
    private static List<MemoryNode>? MemoriesForThisBeat(TurnContext ctx, Character npc)
    {
        if (ctx.NarrativeVector is not { Length: > 0 } vector)
        {
            return null;
        }

        var matched = (npc.Psychology?.Memories ?? []).Values
            .Where(m => m.SemanticVector is { Length: > 0 })
            .Select(m => (Memory: m, Score: SemanticEnrichmentHelper.CosineSimilarity(vector, m.SemanticVector!)))
            .Where(x => x.Score >= CardMemoryMatchThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Memory)
            .ToList();
        return matched.Count > 0 ? matched : null;
    }

    /// <summary>A present NPC as the scene roster lists them: who, doing what, in what mood, plus a short
    /// hook until their card has been sent.</summary>
    /// <param name="keepChanges">Delta-trimmed rows keep what changed this turn (need movers, gear, looks).</param>
    private static NpcPresenceSummary RosterEntry(NpcPresenceSummary n, bool withNote, bool keepChanges) => n with
    {
        KnownNeeds = keepChanges ? n.KnownNeeds : new Dictionary<string, float>(),
        NeedDescriptors = keepChanges ? n.NeedDescriptors : new Dictionary<string, string>(),
        BehavioralSummary = null,
        Notes = withNote && !string.IsNullOrWhiteSpace(n.Notes)
            ? TextTruncation.TruncateAtBoundary(n.Notes, RosterNoteCap) is var (text, cut) && cut ? text + "…" : n.Notes
            : null,
        NotesTruncated = null,
        CurrentAppearance = keepChanges ? n.CurrentAppearance : null,
        VisualTags = keepChanges ? n.VisualTags : null,
        DistinctiveFeatures = keepChanges ? n.DistinctiveFeatures : null,
        Stats = keepChanges ? n.Stats : null,
        BehavioralTension = keepChanges ? n.BehavioralTension : null,
        ActiveInitiatives = keepChanges || n.TurnIntent != null ? n.ActiveInitiatives : null,
        RelevantMemories = null,
        EquippedItems = keepChanges ? n.EquippedItems : null,
        CarriedItems = keepChanges ? n.CarriedItems : null,
        CompressedMemories = null
    };

    /// <summary>npcs[] carries an NPC only when this beat changed something the model narrates (mood,
    /// activity, looks, gear) or the NPC is about to act. Party members travel in the party sections; a
    /// fresh card already covers looks and gear; tension-only initiative rows and behavioralSummary were
    /// restating what the roster and the model's own narrative already say.</summary>
    private void FilterNpcEcho(TurnContext ctx, HashSet<string> partyIds, HashSet<string> cardedIds)
    {
        foreach (var npc in ctx.Result.Npcs ?? [])
        {
            npc.BehavioralSummary = null; // Restated mood + activity + the last event; never needed.
        }

        if (ctx.Mode != TurnMode.Delta || ctx.Result.Npcs is not { Count: > 0 } npcs)
        {
            return;
        }

        var requested = new HashSet<string>(ctx.Request?.ExtraCharacterIds ?? [], StringComparer.OrdinalIgnoreCase);
        var kept = new List<NpcSummaryView>();
        foreach (var npc in npcs)
        {
            if (requested.Contains(npc.CharacterId))
            {
                kept.Add(npc); // Asked for by name: never filtered.
                continue;
            }

            if (partyIds.Contains(npc.CharacterId))
            {
                continue;
            }

            if (npc.Initiative is { } initiative && initiative.TurnIntent == null && initiative.ActiveInitiatives is not { Count: > 0 })
            {
                npc.Initiative = null;
            }

            var moodOrActivity = ctx.AppliedChanges.Any(c => AffectsMoodOrActivity(c, npc.CharacterId));
            var looks = ctx.AppliedChanges.Any(c => AffectsAppearance(c, npc.CharacterId));
            var gear = ctx.AppliedChanges.Any(c => AffectsGearOrStats(ctx, c, npc.CharacterId));
            var carded = cardedIds.Contains(npc.CharacterId);

            if (!moodOrActivity)
            {
                npc.CurrentMood = null;
                npc.CurrentActivity = null;
            }

            if (carded || !looks)
            {
                npc.CurrentAppearance = null;
            }

            if (carded || !gear)
            {
                npc.Equipped = null;
                npc.Carried = null;
            }

            if (npc.CurrentMood != null || npc.CurrentActivity != null || npc.CurrentAppearance != null
                || npc.Equipped != null || npc.Carried != null || npc.Initiative != null || npc.MemoryHint != null
                || npc.KnownNeeds.Count > 0)
            {
                kept.Add(npc);
            }
        }

        ctx.Result.Npcs = kept.Count > 0 ? kept : null;
    }

    /// <summary>An arrival's (fullScene) description and recent events go out on the first visit this
    /// session; a revisit with the same description sends exits and roster only (the description is
    /// already in the conversation, and a compaction or new session clears the ledger). A changed
    /// description is a new key, so it goes out again. Delta scenes have their own unchanged-location trim.</summary>
    private static async Task BriefLocationsAsync(TurnContext ctx)
    {
        if (ctx.Result.FullScene is not { } full || !full.IsLocationAnchored)
        {
            return;
        }

        var key = full.Location.Id + "#" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.Location.Description ?? "")))[..12];
        if (ctx.Cursor.BriefedLocationIds.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(full.Location.Description))
            {
                full.Location = full.Location with { Description = "(described earlier this session)", DescriptionTruncated = null };
            }

            full.RecentEventSummaries = [];
            return;
        }

        ctx.Cursor.BriefedLocationIds.Add(key);

        // T5c: the first visit also carries the DM-only secrets line (hidden ways, concealed items, traps).
        var secrets = await HiddenContent.DmSecretsAsync(ctx.Session, full.Location.Id, CancellationToken.None);
        if (secrets.Count > 0)
        {
            full.DmOnly = secrets;
        }
    }

    /// <summary>A refetched delta scene with no news is dropped: location trimmed, no rumor or combat, no
    /// NPC whose mood/activity changed or who is about to act, and nobody came or went (or only party
    /// members left a place the party no longer is, i.e. the party's own move). A scene the caller asked
    /// for always stays.</summary>
    private static void DropEmptyDeltaScenes(TurnContext ctx, IReadOnlyList<Character> party)
    {
        if (ctx.Mode != TurnMode.Delta || ctx.Result.Scenes is not { Count: > 0 } scenes)
        {
            return;
        }

        var requested = new HashSet<string>(ctx.Request?.ExtraLocationIds ?? [], StringComparer.OrdinalIgnoreCase);
        var partyHere = new HashSet<string>(party.Select(p => p.CurrentLocationId).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var partyIds = new HashSet<string>(party.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        bool RosterNews(string locationId) =>
            ctx.RosterChangesByLocationId.TryGetValue(locationId, out var changed) && changed.Count > 0
            && (partyHere.Contains(locationId) || changed.Any(id => !partyIds.Contains(id)));

        var kept = scenes.Where(s =>
                requested.Contains(s.Location.Id)
                || RosterNews(s.Location.Id)
                || !string.IsNullOrEmpty(s.Location.Description)
                || s.Location.Exits.Count > 0
                || s.LocalRumors.Any()
                || s.ActiveCombat
                || s.PresentNPCs.Any(n => n.CurrentMood != null || n.CurrentActivity != null || n.TurnIntent != null))
            .ToList();
        ctx.Result.Scenes = kept.Count > 0 ? kept : null;
    }
}
