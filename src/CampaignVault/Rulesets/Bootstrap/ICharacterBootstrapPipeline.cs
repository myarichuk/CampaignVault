namespace CampaignVault.Rulesets.Bootstrap;

public interface ICharacterBootstrapPipeline
{
    IReadOnlyList<IBootstrapStep> Steps { get; }
    IReadOnlyList<ILevelGainStep> LevelGainSteps { get; }
}

public sealed class CharacterBootstrapPipeline : ICharacterBootstrapPipeline
{
    public CharacterBootstrapPipeline(
        IEnumerable<IBootstrapStep> steps,
        IEnumerable<ILevelGainStep>? levelGainSteps = null)
    {
        Steps = steps.ToList();
        LevelGainSteps = levelGainSteps?.ToList() ?? [];
    }

    public IReadOnlyList<IBootstrapStep> Steps { get; }
    public IReadOnlyList<ILevelGainStep> LevelGainSteps { get; }
}

public sealed class NullCharacterBootstrapPipeline : ICharacterBootstrapPipeline
{
    public static NullCharacterBootstrapPipeline Instance { get; } = new();

    public IReadOnlyList<IBootstrapStep> Steps { get; } = [];
    public IReadOnlyList<ILevelGainStep> LevelGainSteps { get; } = [];
}