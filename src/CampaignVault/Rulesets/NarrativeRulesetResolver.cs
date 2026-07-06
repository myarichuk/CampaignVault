using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using CampaignVault.Rulesets.Bootstrap;
using Raven.Client.Documents.Session;

namespace CampaignVault.Rulesets;

public class NarrativeRulesetResolver : IRulesetModule, IActionResolution, ICombatRuleset
{
    private readonly IRollService _rollService;

    public NarrativeRulesetResolver(IRollService rollService)
    {
        _rollService = rollService ?? throw new ArgumentNullException(nameof(rollService));
    }

    public RulesetSystem System => RulesetSystem.Narrative;

    public IActionResolution Actions => this;

    public ICombatRuleset Combat => this;

    public ICharacterBootstrapPipeline Bootstrap => NullCharacterBootstrapPipeline.Instance;

    public IEnumerable<IRulesetPressureContributor> PressureContributors => [];

    public async Task<ResolverOutput> ResolveAsync(ChangeContext context, RulesetAction action, CancellationToken ct = default)
    {
        var mutations = new List<WorldChange>();
        
        // Oracle Roll: 1d6
        var oracleReq = new RollRequest
        {
            Mechanic = DiceMechanic.Standard,
            Expression = "1d6",
            Tag = "Oracle"
        };

        var oracleRoll = await _rollService.RollAsync(oracleReq, ct);
        int result = oracleRoll.Result;

        bool success = result >= 4;
        string narrative = string.Empty;

        switch (result)
        {
            case 1:
                narrative = "No, And... The action fails spectacularly, causing a new complication.";
                mutations.Add(new NeedChange { CharacterId = action.CharacterId, Need = "stress", Delta = 20f });
                break;
            case 2:
                narrative = "No. The action simply fails without further complication.";
                break;
            case 3:
                narrative = "No, But... The action fails, but a silver lining or brief advantage is gained.";
                break;
            case 4:
                narrative = "Yes, But... The action succeeds, but at a cost or with a complication.";
                mutations.Add(new NeedChange { CharacterId = action.CharacterId, Need = "stress", Delta = 10f });
                break;
            case 5:
                narrative = "Yes. The action succeeds cleanly.";
                break;
            case 6:
            default:
                narrative = "Yes, And... The action succeeds brilliantly, granting an unexpected advantage.";
                break;
        }

        narrative = $"Narrative Oracle Result: {narrative} (Roll: {result})";

        // For attacks, apply a simple wound/stress mechanic
        if (action.ActionType == RulesetActionType.Attack || action.ActionType == RulesetActionType.Spell)
        {
            if (success && action.TargetIds != null && action.TargetIds.Count > 0)
            {
                foreach (var targetId in action.TargetIds)
                {
                    // A single "Wound" or HP tick
                    mutations.Add(new HpChange { CharacterId = targetId, Delta = -1 });
                }
            }
        }
        else if (action.ActionType == RulesetActionType.Recovery)
        {
            if (success)
            {
                var targets = action.TargetIds != null && action.TargetIds.Count > 0 ? action.TargetIds : new List<string> { action.CharacterId };
                foreach (var targetId in targets)
                {
                    mutations.Add(new HpChange { CharacterId = targetId, Delta = 1 });
                }
            }
        }

        return new ResolverOutput
        {
            Result = success ? ResolverResult.Ok(narrative) : ResolverResult.Fail("oracle_fail", narrative),
            Mutations = mutations
        };
    }

    public async Task<float> RollInitiativeAsync(IAsyncDocumentSession session, string characterId, CancellationToken ct = default)
    {
        var rollReq = new RollRequest { Expression = "1d20", Mechanic = DiceMechanic.Standard };
        var roll = await _rollService.RollAsync(rollReq, ct);
        return roll.Result;
    }

    public async Task<float> RollInitiativeAsync(Character character, CancellationToken ct = default)
    {
        var rollReq = new RollRequest { Expression = "1d20", Mechanic = DiceMechanic.Standard };
        var roll = await _rollService.RollAsync(rollReq, ct);
        return roll.Result;
    }

    public IReadOnlyDictionary<string, int> GetTurnActionBudget(Character character)
    {
        return new Dictionary<string, int>();
    }

    public bool TryConsumeActionSlot(CombatantState state, RulesetAction action, out string? errorReason)
    {
        errorReason = null;
        return true;
    }

    public bool EnforcesRange => false;
}
