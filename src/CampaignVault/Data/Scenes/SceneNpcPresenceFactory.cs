using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

public sealed class SceneNpcPresenceFactory
{
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly INpcInitiativeService _initiativeService;

    public SceneNpcPresenceFactory(
        INpcBehaviorSynthesizer behaviorSynthesizer,
        INpcInitiativeService initiativeService)
    {
        _behaviorSynthesizer = behaviorSynthesizer;
        _initiativeService = initiativeService;
    }

    public List<NpcPresenceSummary> Create(SceneNpcPresenceContext context)
    {
        var presenceSummaries = new List<NpcPresenceSummary>();

        foreach (var npc in context.PresentNpcs)
        {
            // Some persisted NPC documents deserialize with Needs/Psychology null (predates those
            // fields, or written by a path that skipped them) despite the C# `= new()` defaults —
            // mirrors the same defensive null-coalescing in CampaignRepository.BuildNpcSummaryAsync.
            var knownNeeds = (npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>())
                .Where(kv => Math.Round(kv.Value) > 0)
                .ToDictionary(kv => kv.Key, kv => (float)Math.Round(kv.Value));
            // Only this NPC's custom overrides travel here — campaign-wide descriptor text (e.g.
            // stress/fatigue) is shared by every present NPC and goes once into the scene-level
            // NeedDescriptorLegend instead (SceneAssembler.Assemble), not repeated per NPC.
            var needDescriptors = (npc.Needs?.NeedDescriptors ?? new Dictionary<string, string>())
                .Where(kv => !context.GlobalNeedDescriptors.TryGetValue(kv.Key, out var global) || global != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var initiativeContext = new NpcInitiativeContext
            {
                Npc = npc,
                Location = context.Location,
                PresentEntities = context.PresentNpcs,
                RecentEvents = context.RecentSceneEvents,
                NpcRecentEvents = context.RecentCampaignEvents
                    .Where(e => e.Involved.Contains(npc.Id))
                    .ToList(),
                NpcHeldItems = context.ItemsByHolder.GetValueOrDefault(npc.Id) ?? [],
                Config = context.Config,
                CurrentDay = context.Time.TotalDaysElapsed,
                SurfacedViaTool = "get_scene",
                IncludeTensionBreakdown = false
            };
            var enrichment = _initiativeService.Enrich(initiativeContext, context.Campaign);

            var heldItems = context.ItemsByHolder.GetValueOrDefault(npc.Id) ?? [];
            var equippedItems = heldItems.Where(i => i.IsEquipped).Select(ItemSummaryView.From).ToList();
            var carriedItems = heldItems.Where(i => !i.IsEquipped).Select(ItemSummaryView.From).ToList();

            var (notes, notesTruncated) = TextTruncation.TruncateAtBoundary(npc.Notes, context.Config.NpcPresenceNotesCharCap);

            presenceSummaries.Add(new NpcPresenceSummary(
                Id: npc.Id,
                Name: npc.Name,
                CurrentActivity: npc.CurrentActivity ?? "Idle at default location",
                CurrentMood: npc.Psychology?.CurrentMood,
                KnownNeeds: knownNeeds,
                NeedDescriptors: needDescriptors,
                BehavioralSummary: null,
                Notes: notes,
                NotesTruncated: string.IsNullOrEmpty(notes) ? null : notesTruncated,
                KeepAlive: npc.KeepAlive,
                IsPc: npc.IsPc,
                IsPartyCompanion: npc.IsPartyCompanion,
                CurrentAppearance: npc.CurrentAppearance,
                VisualTags: npc.VisualTags,
                DistinctiveFeatures: npc.DistinctiveFeatures,
                TagProvenance: npc.TagProvenance,
                Memories: npc.Psychology?.Memories ?? new Dictionary<string, MemoryNode>(),
                SystemStats: npc.SystemStats,
                Stats: NpcStatLine.From(npc.SystemStats),
                BehavioralTension: Math.Round(enrichment.BehavioralTension),
                ActiveInitiatives: enrichment.ActiveInitiatives.ToList(),
                RelevantMemories: enrichment.RelevantMemories.Take(2).ToList(),
                EquippedItems: equippedItems,
                CarriedItems: carriedItems,
                TurnIntent: enrichment.TurnIntent
            ));
        }

        return presenceSummaries;
    }
}
