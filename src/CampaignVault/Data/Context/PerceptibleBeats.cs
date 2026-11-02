using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;

namespace CampaignVault.Data.Context;

/// <summary>One thing in a batch that others nearby could see or hear (WITNESS_CHANNEL_PLAN.md W1, S1).</summary>
public sealed record PerceptibleBeat(
    string ActorId,
    string Cause,
    Perceptible Signal,
    IReadOnlyCollection<string> Excluded,
    bool CombatNoise,
    string? LocationId = null,
    bool Draws = false);

/// <summary>
/// Finds the perceptible beats in a batch: a spell that is loud or visible, the noise of a fight, a death, a performance or any action or
/// event marked perceptible or attention-drawing. Shared by the witness line (who noticed) and the dispatcher (who it woke).
/// </summary>
public sealed class PerceptibleBeats(SpellDefinitionProvider spells)
{
    public IEnumerable<PerceptibleBeat> Detect(IEnumerable<WorldChange> changes, string? system)
    {
        foreach (var change in changes)
        {
            switch (change)
            {
                case RulesetAction { ActionType: RulesetActionType.Spell } cast:
                {
                    SpellDefinition? spell = null;
                    if (system != null && !string.IsNullOrWhiteSpace(cast.ActionName))
                    {
                        spells.TryGet(system, CastingComponentGate.NormalizeSlug(cast.ActionName), out spell);
                    }

                    var signal = Perceptibility.OfSpell(spell, cast);
                    if (signal != Perceptible.None)
                    {
                        yield return new PerceptibleBeat(cast.CharacterId, cast.ActionName.Replace('_', ' ').ToLowerInvariant(), signal, cast.TargetIds, false);
                    }

                    break;
                }
                case RulesetAction { ActionType: RulesetActionType.Attack } attack:
                    yield return new PerceptibleBeat(attack.CharacterId, "fighting", Perceptible.Both, attack.TargetIds, true);
                    break;
                case RulesetAction action when CrowdDraw.Draws(action) || action.Parameters.ContainsKey("perceptible"):
                {
                    // A performance, haggling at full voice, a juggling act: S1's social spectacle.
                    var draws = CrowdDraw.Draws(action);
                    var signal = CrowdDraw.Signal(action.Parameters.GetValueOrDefault("perceptible"), draws);
                    if (signal != Perceptible.None)
                    {
                        yield return new PerceptibleBeat(action.CharacterId, action.ActionName.ToLowerInvariant(), signal, action.TargetIds, false,
                            Draws: draws);
                    }

                    break;
                }
                case DeathChange { Revive: false } death:
                    yield return new PerceptibleBeat(death.CharacterId, "dies", Perceptible.Visible, [death.KillerId ?? ""], false);
                    break;
                case EventOccurred ev when CrowdDraw.Signal(ev.Perceptible, CrowdDraw.Draws(ev)) != Perceptible.None:
                    var actor = ev.InitiatorId ?? ev.Involved?.FirstOrDefault() ?? "";
                    yield return new PerceptibleBeat(actor, Short(ev.Summary), CrowdDraw.Signal(ev.Perceptible, CrowdDraw.Draws(ev)), ev.Involved ?? [],
                        false, ev.LocationId, CrowdDraw.Draws(ev));
                    break;
            }
        }
    }

    public static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "something" : text.Length <= 60 ? text.TrimEnd('.') : text[..57] + "...";
}
