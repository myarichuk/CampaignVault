using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

internal static class CharacterBootstrapApplier
{
    public static async Task ApplyCreationBootstrapAsync(
        CharacterBootstrapOrchestrator bootstrap,
        Character character,
        RulesetSystem activeSystem,
        int? commitMaxHp,
        int? commitCurrentHp,
        BootstrapTrigger trigger,
        ChangeContext context,
        HitPointDerivationMode? hpModeOverride = null,
        CancellationToken ct = default)
    {
        var hp = BootstrapHpResolver.Resolve(character, commitMaxHp, commitCurrentHp);
        var report = await bootstrap.ApplyCreationAsync(new BootstrapContext
        {
            Character = character,
            ActiveSystem = activeSystem,
            ExplicitMaxHp = hp.ExplicitMaxHp,
            ExplicitCurrentHp = hp.ExplicitCurrentHp,
            HpModeOverride = hpModeOverride,
            Trigger = trigger,
            Session = context.Session,
            CampaignName = context.CampaignName,
        }, ct);

        CharacterCreateHandler.RecordBootstrapReport(context, report);
    }
}