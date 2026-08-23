using CampaignVault.Data.Initiative;
using CampaignVault.Models;

namespace CampaignVault.Data.Scenes;

public sealed class SceneAssembler
{
    private readonly SceneNpcMerger _npcMerger;
    private readonly SceneNpcPresenceFactory _npcPresenceFactory;
    private readonly SceneFactionSummaryFactory _factionSummaryFactory;

    public SceneAssembler(
        INpcBehaviorSynthesizer behaviorSynthesizer,
        INpcInitiativeService initiativeService,
        SceneNpcMerger? npcMerger = null,
        SceneFactionSummaryFactory? factionSummaryFactory = null)
    {
        _npcMerger = npcMerger ?? new SceneNpcMerger();
        _npcPresenceFactory = new SceneNpcPresenceFactory(behaviorSynthesizer, initiativeService);
        _factionSummaryFactory = factionSummaryFactory ?? new SceneFactionSummaryFactory();
    }

    public SceneView CreateUnanchoredScene(string locationId)
    {
        return new SceneView
        {
            Location = LocationDetailView.From(new Location
            {
                Id = locationId,
                Name = "[Unanchored]",
                Description = "This location does not exist in the persistent world model yet.",
                Type = LocationType.Room,
                Exits = [],
                PointsOfInterest = [],
                PointOfInterestDetails = new(StringComparer.OrdinalIgnoreCase),
                AmbientCrowd = null,
                LastVisitedDay = null
            }),
            PresentNPCs = [],
            LocalRumors = [],
            VisibleItems = [],
            RecentEvents = [],
            RecentEventSummaries = [],
            ActiveCombat = null,
            IsLocationAnchored = false,
            ActiveQuests = [],
            RelevantFactions = [],
            LastKnownTravel = null,
            SuggestedCommitExamples = [],
            TurnIntentCharacterId = null
        };
    }

    public SceneView Assemble(SceneAssemblyContext context)
    {
        var presentNpcs = _npcMerger.Merge(
            context.NpcsFromIndex,
            context.NpcsFromSimulation,
            context.EffectiveCampaign);

        var presenceSummaries = _npcPresenceFactory.Create(new SceneNpcPresenceContext
        {
            PresentNpcs = presentNpcs,
            Location = context.Location,
            RecentSceneEvents = context.Events,
            RecentCampaignEvents = context.RecentCampaignEvents,
            ItemsByHolder = context.ItemsByHolder,
            GlobalNeedDescriptors = context.GlobalNeedDescriptors,
            Time = context.Time,
            Config = context.Config,
            Campaign = context.Campaign
        });

        // Generate recognition hints for PCs based on their skills/background vs. location/NPC features
        var recognitionHints = SceneRecognitionHintFactory.Create(context.Location, presenceSummaries);

        if (context.MarkVisited)
        {
            context.Location.LastVisitedDay = context.Time.TotalDaysElapsed;
        }

        // Advisory only — the present NPC with the most pressing initiative, or null ("open turn") if
        // none of them currently think it's their move. Never a hard gate, unlike combat's ActiveTurnId.
        var turnIntentHolder = presenceSummaries
            .Where(n => n.TurnIntent?.Holder == "npc")
            .OrderByDescending(n => n.BehavioralTension)
            .FirstOrDefault();

        return new SceneView
        {
            Location = LocationDetailView.From(context.Location),
            PresentNPCs = presenceSummaries,
            LocalRumors = context.Rumors.Select(r => new RumorSummary(r.Id, r.Subject, r.CurrentText, r.State)).ToList(),
            VisibleItems = context.Items.Select(ItemSummaryView.From).ToList(),
            RecentEvents = context.Events,
            RecentEventSummaries = context.Events.Select(EventSummaryView.From).ToList(),
            ActiveCombat = NormalizeActiveCombat(context.ActiveCombat, context.Location.Id),
            IsLocationAnchored = true,
            ActiveQuests = context.ActiveQuests.Select(CampaignRepository.ToActiveQuestSummary).ToList(),
            RelevantFactions = _factionSummaryFactory.Create(context.RelevantFactions, presentNpcs),
            LastKnownTravel = SceneTravelSummaryExtractor.GetLastKnownTravel(context.Events),
            RecognitionHints = recognitionHints.Count > 0 ? recognitionHints : null,
            ContainerContents = context.ContainerContents.ToList(),
            SuggestedCommitExamples = [],
            TurnIntentCharacterId = turnIntentHolder?.Id
        };
    }

    private static CombatEncounterView? NormalizeActiveCombat(CombatEncounter? activeCombat, string locationId)
    {
        if (activeCombat == null || !activeCombat.IsActive || activeCombat.LocationId != locationId)
        {
            return null;
        }

        return CombatEncounterView.From(activeCombat);
    }
}
