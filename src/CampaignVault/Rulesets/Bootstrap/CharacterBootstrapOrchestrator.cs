namespace CampaignVault.Rulesets.Bootstrap;

public sealed class CharacterBootstrapOrchestrator(IRulesetModuleSelector rulesets)
{
    public async Task<BootstrapReport> ApplyCreationAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var hp = BootstrapHpResolver.Resolve(context.Character, context.ExplicitMaxHp, context.ExplicitCurrentHp);
        BootstrapHpResolver.ApplyExplicitHp(context.Character, hp);

        var pipeline = rulesets.GetModule(context.ActiveSystem).Bootstrap;
        var results = new List<BootstrapStepResult>();

        foreach (var step in pipeline.Steps)
        {
            var stepContext = WithHpResolution(context, hp);
            if (!step.CanApply(stepContext))
            {
                continue;
            }

            var result = await step.ApplyAsync(stepContext, ct);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        if (context.Character.MaxHp > 0 && context.Character.CurrentHp <= 0)
        {
            context.Character.CurrentHp = hp.ExplicitCurrentHp ?? context.Character.MaxHp;
        }

        return new BootstrapReport { Steps = results };
    }

    public async Task<BootstrapReport> ApplyLevelGainAsync(BootstrapContext context, CancellationToken ct = default)
    {
        var pipeline = rulesets.GetModule(context.ActiveSystem).Bootstrap;
        var results = new List<BootstrapStepResult>();
        var hp = BootstrapHpResolver.Resolve(context.Character, null, null, useStoredMaxHpAsOverride: false);

        foreach (var step in pipeline.LevelGainSteps)
        {
            var stepContext = WithHpResolution(context, hp);
            if (!step.CanApply(stepContext))
            {
                continue;
            }

            var result = await step.ApplyLevelGainAsync(stepContext, ct);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        BootstrapClassLevelHelper.SyncClassLevelFromStats(
            context.Character,
            context.ClassGained,
            context.LevelsGained);

        return new BootstrapReport { Steps = results };
    }

    private static BootstrapContext WithHpResolution(BootstrapContext context, BootstrapHpResolver.HpResolution hp) =>
        new()
        {
            Character = context.Character,
            ActiveSystem = context.ActiveSystem,
            Trigger = context.Trigger,
            ExplicitMaxHp = hp.ExplicitMaxHp,
            ExplicitCurrentHp = hp.ExplicitCurrentHp,
            LevelsGained = context.LevelsGained,
            ClassGained = context.ClassGained,
            HpModeOverride = context.HpModeOverride,
            Session = context.Session,
            CampaignName = context.CampaignName,
        };
}