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
    /// hold the same participant while this one is active. Skipping the body's combat turn is not enforced yet;
    /// mark the body with a condition on entry so combat rules treat it as helpless.
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
