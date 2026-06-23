namespace CampaignVault.Rulesets.Bootstrap;

public interface IBootstrapStep
{
    string Name { get; }

    bool CanApply(BootstrapContext context);

    Task<BootstrapStepResult?> ApplyAsync(BootstrapContext context, CancellationToken ct = default);
}

public interface ILevelGainStep
{
    string Name { get; }

    bool CanApply(BootstrapContext context);

    Task<BootstrapStepResult?> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default);
}