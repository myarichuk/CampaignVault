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

        // The class entries grow right after the hit point step, which reads them as they were before the level. The steps
        // after it (proficiencies, spellcasting) then see the new class, as a character created at this level would.
        var synced = false;
        void SyncClasses()
        {
            if (synced)
            {
                return;
            }

            synced = true;
            BootstrapClassLevelHelper.SyncClassLevelFromStats(context.Character, context.ClassGained, context.LevelsGained);
        }

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

            if (step.Name.EndsWith("derive_hit_points", StringComparison.Ordinal))
            {
                SyncClasses();
            }
        }

        SyncClasses();

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
            EquipmentAccess = context.EquipmentAccess,
            CampaignName = context.CampaignName,
        };
}