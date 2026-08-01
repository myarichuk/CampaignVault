using CampaignVault.Data.Pressure;

namespace CampaignVault.Data.Guidance.Contributors;

internal sealed class FirstWorldBuildGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 2;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: checks SeedCoverage.Locations == 0
    }
}

internal sealed class SpellcastingGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 3;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: checks party has spell slots and Spell-category action
    }
}

internal sealed class ItemDamageGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 4;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: checks item state degradation
    }
}

internal sealed class PlotThreadStalenessGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 7;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: reuses PlotThreadStalenessContributor detection
    }
}

internal sealed class SystemStatsGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 8;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: reuses IncompleteSystemStatsPressureContributor detection
    }
}

internal sealed class NarrativeFocusGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 9;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: checks Campaign.NarrativeFocus empty after N commits
    }
}

internal sealed class TimeRecordingGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.World;
    public int Order => 10;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return []; // Placeholder: checks minutesElapsed never used but commits > threshold
    }
}
