using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Rulesets.Modes;

/// <summary>
/// A plugin-defined, scene-scoped turn-based activity (crafting, hairstyling, astral combat, ...) that
/// layers on top of the campaign's <see cref="CampaignConfig.ActiveSystem"/> ruleset rather than
/// replacing it. See PLUGIN_SYSTEM_PLAN.md Track B / INTERACTION_MODES_PLAN.md for the full design.
///
/// A mode's actual mutations (its verbs) are ordinary WorldChange subtypes + IWorldChangeHandler pairs
/// registered by the plugin via the existing Autofac convention scanning — deliberately not part of
/// this interface.
/// </summary>
public interface IInteractionMode
{
    /// <summary>Stable identifier used in CampaignConfig.EnabledModeIds and ModeTransitionChange.ModeId.</summary>
    string ModeId { get; }

    string DisplayName { get; }

    /// <summary>
    /// ActiveSystem values this mode is valid under. Empty = system-agnostic (works under any ruleset).
    /// </summary>
    IReadOnlyList<string> CompatibleSystems { get; }

    IModeStateMachine StateMachine { get; }

    /// <summary>
    /// How this mode claims its participants' actions while it is active. Defaults to
    /// <see cref="ModeParticipantClaim.Independent"/>, so modes written against 0.2.0 keep their behavior.
    /// </summary>
    ModeParticipantClaim ParticipantClaim => ModeParticipantClaim.Independent;

    /// <summary>
    /// Optional veto at mode entry, after the host's enablement, compatibility and claim checks.
    /// <paramref name="participantIds"/> is the request as sent; <paramref name="loaded"/> holds every participant
    /// the host could load, keyed by id (case-insensitive) — a requested id missing from it could not be loaded.
    /// Return a message to refuse entry; null or blank allows it. Defaults to allow, so modes written against
    /// 0.5.0 keep their behavior.
    /// </summary>
    string? ValidateEntry(
        IReadOnlyList<string> participantIds,
        IReadOnlyDictionary<string, Character> loaded,
        IChangeContext context) => null;
}

/// <summary>
/// Action discipline for a mode's participants: which activity a character's actions belong to while the
/// mode is active. The host enforces the mode-vs-mode rule at mode entry; the combat-side rules are
/// declared now and enforced by later hosts.
/// </summary>
public enum ModeParticipantClaim
{
    /// <summary>Own clock, no coupling to other modes or combat (out-of-combat crafting).</summary>
    Independent = 0,

    /// <summary>
    /// The character acts in this mode and in combat, and a mode action costs the character's combat action
    /// (crafting a makeshift grenade mid-fight). Combat-budget charging is not enforced yet.
    /// </summary>
    Shared = 1,

    /// <summary>
    /// The character acts only here (astral projection: the mind leaves, the body stays). No other mode may
    /// hold the same participant while this one is active, and combat skips the body's turn (it can still be
    /// targeted; subscribe to <c>core.character_damaged.v1</c> to react).
    /// </summary>
    Exclusive = 2
}

/// <summary>
/// Turn/state-machine primitive shared by every interaction mode — generalizes the
/// CombatEncounter/ICombatRuleset shape (round/turn progression, per-turn action budgets) so mode
/// plugins don't each reinvent "whose turn, how many actions, is this over."
/// </summary>
public interface IModeStateMachine
{
    ModeEncounter CreateEncounter(string locationId, IReadOnlyList<string> participantIds);

    IReadOnlyDictionary<string, int> GetTurnActionBudget(Character participant);

    bool TryConsumeActionSlot(ModeParticipantState state, WorldChange action, out string? errorReason);

    /// <summary>Advances round/turn, including wraparound to the next participant. Returns false if the encounter is not active.</summary>
    bool AdvanceTurn(ModeEncounter encounter);

    bool IsComplete(ModeEncounter encounter, out string? outcomeNarrative);

    /// <summary>
    /// Adds a participant to a running encounter (<c>mode_transition action=join</c>; the host has already checked
    /// the mode's <see cref="IInteractionMode.ValidateEntry"/> for them). The default builds their state the way
    /// <see cref="CreateEncounter"/> does, with every action budget at 0 so they act from the next round, and
    /// appends them after the current order. Override to seed differently (0.8.0).
    /// </summary>
    bool TryAddParticipant(ModeEncounter encounter, string participantId, out string? errorReason)
    {
        errorReason = null;
        if (encounter.Participants.Any(p => string.Equals(p.CharacterId, participantId, StringComparison.OrdinalIgnoreCase)))
        {
            errorReason = $"'{participantId}' is already in this encounter.";
            return false;
        }

        var fresh = CreateEncounter(encounter.LocationId, [participantId]).Participants
            .FirstOrDefault(p => string.Equals(p.CharacterId, participantId, StringComparison.OrdinalIgnoreCase));
        if (fresh is null)
        {
            errorReason = $"The mode could not create state for '{participantId}'.";
            return false;
        }

        foreach (var key in fresh.ActionBudget.Keys.ToList())
        {
            fresh.ActionBudget[key] = 0;
        }

        encounter.Participants.Add(fresh);
        encounter.ActiveTurnId ??= fresh.CharacterId;
        return true;
    }

    /// <summary>
    /// Removes a participant from a running encounter (<c>mode_transition action=leave</c>). The host refuses to
    /// remove the last participant (use <c>exit</c>). If it was their turn the turn passes to whoever now holds
    /// their place in the order; the default does not refill that participant's budget, so override if the mode
    /// hands out per-turn budgets (0.8.0).
    /// </summary>
    bool TryRemoveParticipant(ModeEncounter encounter, string participantId, out string? errorReason)
    {
        errorReason = null;
        var index = encounter.Participants.FindIndex(p =>
            string.Equals(p.CharacterId, participantId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            errorReason = $"'{participantId}' is not in this encounter.";
            return false;
        }

        var wasActive = string.Equals(encounter.ActiveTurnId, encounter.Participants[index].CharacterId,
            StringComparison.OrdinalIgnoreCase);
        encounter.Participants.RemoveAt(index);
        if (encounter.Participants.Count == 0)
        {
            encounter.ActiveTurnId = null;
        }
        else if (wasActive)
        {
            encounter.ActiveTurnId = encounter.Participants[index % encounter.Participants.Count].CharacterId;
        }

        return true;
    }
}

/// <summary>
/// Resolves interaction modes by ID. Mirrors IRulesetModuleSelector's shape, but modes are a stack of
/// independently enabled scene activities rather than one ruleset per campaign.
/// </summary>
public interface IInteractionModeSelector
{
    IInteractionMode? TryGetMode(string modeId);

    IReadOnlyCollection<string> RegisteredModeIds { get; }
}

/// <summary>
/// Default DI-discovered implementation: indexes every registered IInteractionMode (core or plugin) by
/// ModeId. Registered explicitly in ConventionRegistration (not via namespace-matched convention scanning,
/// since this type is not in the CampaignVault.Rulesets namespace that convention matches on).
/// </summary>
public sealed class InteractionModeSelector : IInteractionModeSelector
{
    private readonly Dictionary<string, IInteractionMode> _modesById;

    public InteractionModeSelector(IEnumerable<IInteractionMode> modes)
    {
        _modesById = modes.ToDictionary(m => m.ModeId, StringComparer.OrdinalIgnoreCase);
    }

    public IInteractionMode? TryGetMode(string modeId) =>
        !string.IsNullOrWhiteSpace(modeId) && _modesById.TryGetValue(modeId, out var mode) ? mode : null;

    public IReadOnlyCollection<string> RegisteredModeIds => _modesById.Keys;
}
