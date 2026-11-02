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
    int CrowdModifier = 0,
    bool Luminous = false,
    bool HasAppeal = false)
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
    /// <summary>
    /// Every perceptible beat in the batch. <see cref="PerceptibleBeat.HasAppeal"/> marks one the impression step reports
    /// (IMPRESSIONS_PLAN.md I5), so the witness line leaves it out instead of saying it twice.
    /// </summary>
    public IEnumerable<PerceptibleBeat> Detect(IEnumerable<WorldChange> changes, string? system)
    {
        foreach (var change in changes)
        {
            var appeal = Impressions.Appeal.Of(change) != null;
            foreach (var beat in DetectOne(change, system))
            {
                yield return appeal ? beat with { HasAppeal = true } : beat;
            }
        }
    }

    private IEnumerable<PerceptibleBeat> DetectOne(WorldChange change, string? system)
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
                        Draws: draws, CrowdModifier: CrowdDraw.ModifierOf(cast), Luminous: IsLuminous(spell, cast));
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
                        Draws: draws, CrowdModifier: CrowdDraw.ModifierOf(action), Luminous: IsLuminous(null, action));
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

    /// <summary>Light-giving spells with no damage to say so (SRD 5.1 / Player Core: each sheds light).</summary>
    private static readonly HashSet<string> LightSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "daylight", "dancing_lights", "faerie_fire", "continual_flame", "produce_flame", "flaming_sphere", "moonbeam",
        "sunbeam", "sunburst", "guiding_bolt",
    };

    /// <summary>
    /// I1/D8: a beat that makes its own light is seen at any distance, in any light: fire or radiant damage, a light spell, or
    /// anything marked <c>luminous: true</c>. An illusion's image is not one (SRD 5.1 Minor Illusion: it "can't create ... light").
    /// </summary>
    public static bool IsLuminous(SpellDefinition? spell, RulesetAction action)
    {
        if (string.Equals(action.Parameters.GetValueOrDefault("luminous")?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (action.ActionType != RulesetActionType.Spell)
        {
            return false;
        }

        var type = spell?.DamageType ?? action.DamageType;
        return string.Equals(type, "fire", StringComparison.OrdinalIgnoreCase)
               || string.Equals(type, "radiant", StringComparison.OrdinalIgnoreCase)
               || LightSpells.Contains(CastingComponentGate.NormalizeSlug(action.ActionName));
    }

    public static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "something" : text.Length <= 60 ? text.TrimEnd('.') : text[..57] + "...";
}
