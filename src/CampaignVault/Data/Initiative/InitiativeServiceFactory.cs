namespace CampaignVault.Data.Initiative;

public static class InitiativeServiceFactory
{
    public static INpcInitiativeService CreateDefault(IEnumerable<INpcInitiativeSignalProvider>? providers = null)
    {
        return new NpcInitiativeService(
            providers ?? [],
            new DefaultRelevantMemorySelector(),
            new DefaultBehavioralTensionCalculator(),
            new CampaignInitiativeSuppressionStore());
    }
}