namespace CampaignVault.Data.Initiative;

public static class InitiativeServiceFactory
{
    public static IReadOnlyList<INpcInitiativeSignalProvider> DefaultProviders() =>
    [
        new RelationalInitiativeProvider(),
        new MemoryInitiativeProvider(),
        new NeedActivityConflictProvider(),
        new DispositionInitiativeProvider(),
        new SceneMomentumInitiativeProvider()
    ];

    public static INpcInitiativeService CreateDefault(IEnumerable<INpcInitiativeSignalProvider>? providers = null)
    {
        return new NpcInitiativeService(
            providers ?? DefaultProviders(),
            new DefaultRelevantMemorySelector(),
            new DefaultBehavioralTensionCalculator(),
            new CampaignInitiativeSuppressionStore());
    }
}