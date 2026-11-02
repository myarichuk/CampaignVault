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
    bool Draws = false,
    int CrowdModifier = 0)
{
    /// <summary>
    /// Where the beat happens: the event's own location, then where its actor stands, then the party's. The witness line and the
    /// waking step both use this, so a scream with no initiator wakes the same sleepers the line names.
    /// </summary>
    public string? Where(Character? actor, string? partyLocationId) =>
        LocationId ?? (string.IsNullOrEmpty(actor?.CurrentLocationId) ? partyLocationId : actor.CurrentLocationId);
}

/// <summary>
/// Finds the perceptible beats in a batch: a spell that is loud or visible, the noise of a fight, a death, a performance or any action or
/// event marked perceptible or attention-drawing. Shared by the witness line (who noticed) and the dispatcher (who it woke, and
/// whether it pulls a crowd). <c>parameters.perceptible</c> and <c>attention</c> add to anything, a spell included.
/// </summary>
public sealed class PerceptibleBeats(SpellDefinitionProvider? spells)
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
                    if (spells != null && system != null && !string.IsNullOrWhiteSpace(cast.ActionName))
                    {
                        spells.TryGet(system, CastingComponentGate.NormalizeSlug(cast.ActionName), out spell);
                    }

                    // The spell's data, plus what the narrator marked: Daylight derives none but can be marked visible.
                    var draws = CrowdDraw.Draws(cast);
                    var signal = Perceptibility.OfSpell(spell, cast) | CrowdDraw.Signal(cast.Parameters.GetValueOrDefault("perceptible"), draws);
                    if (signal != Perceptible.None)
                    {
                        yield return new PerceptibleBeat(cast.CharacterId, cast.ActionName.Replace('_', ' ').ToLowerInvariant(), signal, cast.TargetIds, false,
                            Draws: draws, CrowdModifier: CrowdDraw.ModifierOf(cast));
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
                            Draws: draws, CrowdModifier: CrowdDraw.ModifierOf(action));
                    }

                    break;
                }
                case DeathChange { Revive: false } death:
                    yield return new PerceptibleBeat(death.CharacterId, "dies", Perceptible.Visible, [death.KillerId ?? ""], false);
                    break;
                case EventOccurred ev when CrowdDraw.Signal(ev.Perceptible, CrowdDraw.Draws(ev)) != Perceptible.None:
                    var actor = ev.InitiatorId ?? ev.Involved?.FirstOrDefault() ?? "";
                    yield return new PerceptibleBeat(actor, Short(ev.Summary), CrowdDraw.Signal(ev.Perceptible, CrowdDraw.Draws(ev)), ev.Involved ?? [],
                        false, ev.LocationId, CrowdDraw.Draws(ev), ev.CrowdModifier ?? 0);
                    break;
            }
        }
    }

    public static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "something" : text.Length <= 60 ? text.TrimEnd('.') : text[..57] + "...";
}
