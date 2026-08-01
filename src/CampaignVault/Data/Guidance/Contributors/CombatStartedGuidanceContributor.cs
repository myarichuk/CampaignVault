using CampaignVault.Data.Pressure;

namespace CampaignVault.Data.Guidance.Contributors;

/// <summary>
/// Delivers guidance when combat starts (Round == 1), emphasizing proper action resolution patterns.
/// </summary>
internal sealed class CombatStartedGuidanceContributor : IGuidanceContributor
{
    public PressureScope Scope => PressureScope.Scene;
    public int Order => 5;

    public async Task<IEnumerable<GuidanceHint>> EvaluateAsync(PressureContext ctx, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // Check if combat is active and at round 1 (first combat turn)
        if (ctx.Scene?.ActiveCombat == null || ctx.Scene.ActiveCombat.Round != 1)
            return [];

        return new[]
        {
            new GuidanceHint(
                Key: "combat.first-round",
                Text: "Resolve every action through ruleset_action ($type: 'ruleset_action'), not separate hp/status changes. The engine applies effects automatically.",
                Trigger: GuidanceTrigger.CombatStarted,
                Priority: 8)
            {
                Example = """{"$type": "ruleset_action", "action": "attack", "characterId": "chars/x", "targetId": "chars/y"}"""
            }
        };
    }
}
