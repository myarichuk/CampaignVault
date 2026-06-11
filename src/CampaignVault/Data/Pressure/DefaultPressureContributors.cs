using CampaignVault.Data.Pressure.Contributors;

namespace CampaignVault.Data.Pressure;

public static class DefaultPressureContributors
{
    public static IEnumerable<IPressureContributor> All() =>
    [
        new AgingRumorPressureContributor(),
        new UnresolvedEventPressureContributor(),
        new CharacterDistressPressureContributor(),
        new DanglingItemPressureContributor(),
        new NeverVisitedTransientPressureContributor(),
        new QuestDeadlinePressureContributor(),
        new StuckTravelPressureContributor(),
        new PressureHintEnricher(),
        new LocationHallucinationPressureContributor(),
        new LocationIntegrityPressureContributor(),
        new LocationConnectivityPressureContributor(),
        new LocationFlavorPressureContributor(),
        new SceneQuestStalenessPressureContributor(),
        new TransientQuestGiverPressureContributor(),
        new MemoryDecayPressureContributor(),
        new UrgentInitiativePressureContributor(),
        new FactionTerritoryPressureContributor(),
        new FactionOpportunisticPressureContributor(),
        new FactionEconomyPressureContributor(),
        new FactionRecentEventPressureContributor(),
        new EngagementRelationPressureContributor()
    ];
}