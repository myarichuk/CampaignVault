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
            var knownNeeds = npc.Needs.ActiveNeeds.ToDictionary(kv => kv.Key, kv => kv.Value);
            var needDescriptors = new Dictionary<string, string>(context.GlobalNeedDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npc.Needs.NeedDescriptors)
            {
                needDescriptors[kv.Key] = kv.Value;
            }

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

            presenceSummaries.Add(new NpcPresenceSummary(
                Id: npc.Id,
                Name: npc.Name,
                CurrentActivity: npc.CurrentActivity ?? "Idle at default location",
                CurrentMood: npc.Psychology.CurrentMood,
                KnownNeeds: knownNeeds,
                NeedDescriptors: needDescriptors,
                BehavioralSummary: _behaviorSynthesizer.GenerateSummary(npc, context.Time, context.RecentSceneEvents),
                Notes: npc.Notes,
                KeepAlive: npc.KeepAlive,
                IsPc: npc.IsPc,
                IsPartyCompanion: npc.IsPartyCompanion,
                CurrentAppearance: npc.CurrentAppearance,
                VisualTags: npc.VisualTags,
                DistinctiveFeatures: npc.DistinctiveFeatures,
                TagProvenance: npc.TagProvenance,
                Memories: npc.Psychology.Memories,
                SystemStats: npc.SystemStats,
                BehavioralTension: enrichment.BehavioralTension,
                ActiveInitiatives: enrichment.ActiveInitiatives.ToList(),
                RelevantMemories: enrichment.RelevantMemories.ToList(),
                EquippedItems: equippedItems,
                CarriedItems: carriedItems,
                TurnIntent: enrichment.TurnIntent
            ));
        }

        return presenceSummaries;
    }
}
