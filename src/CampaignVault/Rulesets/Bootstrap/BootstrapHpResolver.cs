using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Resolves whether HP is derived by the pipeline or taken from an explicit stat-block override.
/// PCs should omit both <c>maxHp</c> and <c>statBlockHp</c>; creature stat blocks use either.
/// </summary>
public static class BootstrapHpResolver
{
    public sealed record HpResolution(int? ExplicitMaxHp, int? ExplicitCurrentHp)
    {
        public bool HasExplicitMaxHp => ExplicitMaxHp is > 0;
    }

    public static HpResolution Resolve(
        Character character,
        int? commitMaxHp,
        int? commitCurrentHp,
        bool useStoredMaxHpAsOverride = true)
    {
        var statBlockHp = character.SystemStats?.StatBlockHp;
        int? explicitMax = commitMaxHp is > 0 ? commitMaxHp
            : statBlockHp is > 0 ? statBlockHp
            : useStoredMaxHpAsOverride
              && character.MaxHp > 0
              && commitMaxHp is null
              && statBlockHp is null or <= 0
                ? character.MaxHp
                : null;

        int? explicitCurrent = commitCurrentHp;
        if (explicitCurrent is null or <= 0 && explicitMax is > 0 && character.CurrentHp > 0)
        {
            explicitCurrent = character.CurrentHp;
        }

        return new HpResolution(explicitMax, explicitCurrent);
    }

    public static void ApplyExplicitHp(Character character, HpResolution resolution)
    {
        if (!resolution.HasExplicitMaxHp)
        {
            return;
        }

        character.MaxHp = resolution.ExplicitMaxHp!.Value;
        if (resolution.ExplicitCurrentHp is > 0)
        {
            character.CurrentHp = resolution.ExplicitCurrentHp.Value;
        }
        else if (character.CurrentHp <= 0)
        {
            character.CurrentHp = resolution.ExplicitMaxHp.Value;
        }
    }
}