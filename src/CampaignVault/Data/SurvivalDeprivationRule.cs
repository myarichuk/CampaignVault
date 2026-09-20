using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data;

/// <summary>
/// Turns sustained deprivation/exposure — hunger, thirst, or extreme felt temperature staying severe
/// for too many consecutive in-game days — into a deterministic, ruleset-driven consequence, instead
/// of leaving it to the DM-LLM's discretion (the existing pattern for <see cref="NeedsAccumulationRule"/>
/// and <see cref="ClimateExposureRule"/>, both of which only ever compute readings and surface advisory
/// pressure text).
///
/// GENERIC BY DESIGN: this rule knows nothing D&D-specific. It tracks three signals the same way
/// (hunger, thirst, temperature) and, once a per-signal "severe for N consecutive days" streak crosses
/// a configurable tolerance (<see cref="CampaignConfig.DeprivationToleranceDaysFood"/> etc.), asks the
/// active ruleset system for whichever <see cref="ConditionDefinition"/> it has marked
/// <see cref="ConditionDefinition.IsSurvivalExhaustion"/> and applies/escalates that — dnd5e's stacking
/// "exhaustion", pf2e's non-stacking "fatigued", or (for a system with none marked, e.g. swade, which
/// ships no condition YAMLs yet) nothing at all. That's a deliberate no-op, not a bug: this rule never
/// invents rules content for a system that hasn't defined its own exhaustion-equivalent condition.
///
/// STREAK STORAGE: reuses the existing free-form <see cref="SystemExtension.Attributes"/> bag (the same
/// store already used for "exhaustion_level" and "morale") under keys "deprivation_streak_hunger",
/// "deprivation_streak_thirst", "deprivation_streak_temperature" — no new model fields needed. A streak
/// grows while its signal stays severe, holds steady while the signal is between "severe" and
/// "recovered" (avoids flapping right at the threshold), and resets to zero once the character actually
/// recovers (eats/drinks enough, or gets back into a safe temperature band).
/// </summary>
public class SurvivalDeprivationRule : ISimulationRule
{
    private readonly ConditionDefinitionProvider _conditionProvider;

    public SurvivalDeprivationRule(ConditionDefinitionProvider conditionProvider)
    {
        _conditionProvider = conditionProvider;
    }

    public string Name => "Survival Deprivation";

    // Runs after NeedsAccumulationRule (35) and ClimateExposureRule (37), whose pre-tick readings it
    // consumes — same one-tick lag NeedsAccumulationRule itself accepts elsewhere, harmless at day
    // granularity.
    public int Order => 38;

    public virtual Task<RuleResult> ApplyAsync(SimulationContext context, CancellationToken ct = default)
    {
        var narratives = new List<string>();
        var deltas = new List<WorldChange>();

        if (context.Config?.SurvivalConsequencesEnabled == false)
        {
            return Task.FromResult(new RuleResult(narratives, deltas));
        }

        var days = (float)context.DaysPassed;
        if (days <= 0f)
        {
            return Task.FromResult(new RuleResult(narratives, deltas));
        }

        var toleranceFood = context.Config?.DeprivationToleranceDaysFood ?? 3f;
        var toleranceWater = context.Config?.DeprivationToleranceDaysWater ?? 1f;
        var toleranceTemperature = context.Config?.DeprivationToleranceDaysTemperature ?? 1f;

        foreach (var character in context.ScheduledNpcs)
        {
            if (character.SystemStats == null)
            {
                continue;
            }

            // Dead characters don't starve further (mirrors NeedsAccumulationRule).
            if (character.MaxHp > 0 && character.CurrentHp <= 0)
            {
                continue;
            }

            var hunger = character.Needs?.ActiveNeeds.GetValueOrDefault("hunger") ?? 0f;
            var thirst = character.Needs?.ActiveNeeds.GetValueOrDefault("thirst") ?? 0f;
            var temperature = character.SystemStats.Temperature;

            ProcessSignal(
                character, "hunger", days, toleranceFood,
                severe: hunger >= SurvivalThresholds.SevereHunger,
                recovered: hunger <= SurvivalThresholds.RecoveryHunger,
                causeLabel: "prolonged hunger",
                deltas, narratives);

            ProcessSignal(
                character, "thirst", days, toleranceWater,
                severe: thirst >= SurvivalThresholds.SevereThirst,
                recovered: thirst <= SurvivalThresholds.RecoveryThirst,
                causeLabel: "severe dehydration",
                deltas, narratives);

            var tempSevere = temperature <= SurvivalThresholds.SevereCold || temperature >= SurvivalThresholds.SevereHeat;
            var tempRecovered =
                temperature > SurvivalThresholds.SevereCold + SurvivalThresholds.TemperatureRecoveryMargin &&
                temperature < SurvivalThresholds.SevereHeat - SurvivalThresholds.TemperatureRecoveryMargin;

            ProcessSignal(
                character, "temperature", days, toleranceTemperature,
                severe: tempSevere,
                recovered: tempRecovered,
                causeLabel: "extreme temperature exposure",
                deltas, narratives);
        }

        return Task.FromResult(new RuleResult(narratives, deltas));
    }

    private void ProcessSignal(
        Character character,
        string signal,
        float days,
        float toleranceDays,
        bool severe,
        bool recovered,
        string causeLabel,
        List<WorldChange> deltas,
        List<string> narratives)
    {
        var attrKey = $"deprivation_streak_{signal}";
        var current = character.SystemStats!.Attributes.GetValueOrDefault(attrKey, 0f);

        if (recovered)
        {
            if (current != 0f)
            {
                deltas.Add(new AttributeChange { CharacterId = character.Id, Attribute = attrKey, Value = 0f, IsDelta = false });
            }
            return;
        }

        // Holding pattern: neither severe nor recovered — leave the streak where it is.
        if (!severe)
        {
            return;
        }

        var newStreak = current + days;
        deltas.Add(new AttributeChange { CharacterId = character.Id, Attribute = attrKey, Value = days, IsDelta = true });

        var levelsGained = LevelsPastTolerance(newStreak, toleranceDays) - LevelsPastTolerance(current, toleranceDays);
        if (levelsGained > 0)
        {
            TryEscalate(character, causeLabel, levelsGained, deltas, narratives);
        }
    }

    /// <summary>
    /// "The day it first exceeds tolerance" already counts as one day beyond the limit (DDB's rule:
    /// exhaustion is applied "at the end of each day beyond" the tolerance), so this rounds UP once a
    /// streak clears the tolerance boundary rather than waiting for a full additional day past it.
    /// </summary>
    private static int LevelsPastTolerance(float streakDays, float toleranceDays) =>
        streakDays <= toleranceDays ? 0 : (int)MathF.Ceiling(streakDays - toleranceDays);

    private void TryEscalate(
        Character character,
        string causeLabel,
        int levelsGained,
        List<WorldChange> deltas,
        List<string> narratives)
    {
        if (!RulesetSystemResolver.TryFromStats(character.SystemStats!, out var system) || system == null)
        {
            return;
        }

        var conditions = _conditionProvider.GetConditionsForSystem(system);
        var definition = conditions.Values.FirstOrDefault(c => c.IsSurvivalExhaustion);
        if (definition == null)
        {
            return;
        }

        var existing = character.SystemStats!.StatusEffects
            .FirstOrDefault(e => string.Equals(e.ConditionName, definition.Name, StringComparison.OrdinalIgnoreCase));

        var displayName = char.ToUpperInvariant(definition.Name[0]) + definition.Name[1..];

        if (definition.IsStacking)
        {
            var currentLevel = existing != null && ConditionExpiryEvaluator.TryParseStackLevel(existing.Name, out var parsed)
                ? parsed
                : 0;
            var newLevel = currentLevel + levelsGained;

            if (existing != null)
            {
                deltas.Add(new StatusRemove { CharacterId = character.Id, Status = existing.Name });
            }

            deltas.Add(new StatusChange
            {
                CharacterId = character.Id,
                Effect = new StatusEffect
                {
                    Name = ConditionExpiryEvaluator.FormatStackLevel(displayName, newLevel),
                    Category = "Condition",
                    ConditionName = definition.Name,
                    AppliedBy = "system/survival-deprivation",
                }
            });

            var levelWord = levelsGained == 1 ? "a level" : $"{levelsGained} levels";
            narratives.Add($"{character.Name} gains {levelWord} of {definition.Name} from {causeLabel} (now level {newLevel}).");
        }
        else
        {
            // Non-stacking: already applied, nothing further to escalate.
            if (existing != null)
            {
                return;
            }

            deltas.Add(new StatusChange
            {
                CharacterId = character.Id,
                Effect = new StatusEffect
                {
                    Name = displayName,
                    Category = "Condition",
                    ConditionName = definition.Name,
                    AppliedBy = "system/survival-deprivation",
                }
            });

            narratives.Add($"{character.Name} becomes {definition.Name} from {causeLabel}.");
        }
    }
}
