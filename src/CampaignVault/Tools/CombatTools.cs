using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Events;
using CampaignVault.Models;
using CampaignVault.Plugins;
using CampaignVault.Rulesets;
using CampaignVault.Rulesets.Modes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CombatTools : CampaignToolBase, IMcpServerTool
{
    private readonly IRulesetModuleSelector _rulesetSelector;
    private readonly IInteractionModeSelector? _modeSelector;
    private readonly IEnumerable<IPluginTraitsUpgrader> _traitsUpgraders;

    public CombatTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IRulesetModuleSelector rulesetSelector,
        ILogger<CombatTools>? logger = null,
        IInteractionModeSelector? modeSelector = null,
        IEnumerable<IPluginTraitsUpgrader>? traitsUpgraders = null)
        : base(repository, keys, logger)
    {
        _rulesetSelector = rulesetSelector;
        _modeSelector = modeSelector;
        _traitsUpgraders = traitsUpgraders ?? [];
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("Combat lifecycle by 'action': start (locationId + combatantIds; rolls initiative), next (advance turn), end, status (read-only). Attacks/spells/checks are NOT here: commit them as ruleset_action via take_turn.")]
    public Task<ToolResult<object>> Combat(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("Lifecycle action: 'start', 'next', 'end', or 'status'.")]
        string action,
        [Description("start only: the location ID where combat is happening.")]
        string? locationId = null,
        [Description("start only: character IDs participating in combat, e.g. [\"chars/valen\", \"chars/goblin1\"].")]
        List<string>? combatantIds = null,
        [Description("start only: abandon any active combat and start fresh instead of failing.")]
        bool overwriteActive = false,
        [Description("next only: fail if the current active turn does not match this ID (prevents accidental double-advancing).")]
        string? expectedActiveTurnId = null)
    {
        return (action?.Trim().ToLowerInvariant()) switch
        {
            "start" => Box(StartCombat(locationId ?? "", combatantIds ?? [], campaignName, overwriteActive)),
            "next" => Box(NextTurn(campaignName, expectedActiveTurnId)),
            "end" => Box(EndCombat(campaignName)),
            "status" => GetCombat(campaignName),
            _ => Task.FromResult(new ToolResult<object>(false, Error: ToolErrors.InvalidArgument,
                Summary: $"Unknown combat action '{action}'. Use 'start', 'next', 'end', or 'status'."))
        };
    }

    private static async Task<ToolResult<object>> Box<T>(Task<ToolResult<T>> task)
    {
        var r = await task;
        return new ToolResult<object>(r.Success, r.Data, r.Summary, r.Error, r.WorldPressure, r.RetryExample);
    }

    internal Task<ToolResult<CombatEncounterView>> StartCombat(
        [Description("The location ID where combat is happening.")]
        string locationId,
        [Description("List of character IDs participating in combat.")]
        List<string> combatantIds,
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description("If true, abandon any active combat and start fresh. Otherwise, fails if combat already active.")]
        bool overwriteActive = false)
    {
        if (string.IsNullOrWhiteSpace(locationId))
        {
            return ToolArgumentErrors.Missing<CombatEncounterView>(
                "locationId",
                "Pass where combat occurs.",
                exampleCall: "combat(action: \"start\", locationId: \"locations/tavern\", combatantIds: [\"chars/hero\"])");
        }

        if (combatantIds is null or { Count: 0 })
        {
            return Task.FromResult(new ToolResult<CombatEncounterView>(
                false,
                Error: "InvalidInput",
                Summary: "Cannot start combat with zero combatants. Pass combatantIds (not combatants) — an array of character IDs, e.g. [\"chars/valen\", \"chars/guard\"]."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var combatId = _keys.CombatCurrent(effective);
            var existing = await session.LoadAsync<CombatEncounter>(combatId);
            if (existing?.IsActive == true && !overwriteActive)
            {
                return new ToolResult<CombatEncounterView>(false,
                    Error: $"Combat already active at {existing.LocationId} (round {existing.Round}). " +
                           "Call combat(action: 'end') to abandon, or pass overwriteActive:true to force restart.");
            }
            var abandonedRound = existing?.IsActive == true ? existing.Round : (int?)null;
            var uniqueIds = combatantIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var loadedCharacters = await session.LoadAsync<Character>(uniqueIds);

            // Ensure all loaded characters have upgraded SystemStats (type coercion + SkillModifiers derivation).
            // Initiative rolls and other combat resolution need SkillModifiers to be populated.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, loadedCharacters, effective, _keys, traitsUpgraders: _traitsUpgraders, logger: _logger);

            var validCharacters = new List<Character>();
            var droppedForZeroHp = new List<string>();

            foreach (var id in uniqueIds)
            {
                if (!loadedCharacters.TryGetValue(id, out var character) || character is null)
                {
                    return new ToolResult<CombatEncounterView>(false, Error: "NotFound",
                        Summary: $"Character '{id}' not found.");
                }

                if (!CampaignEntityVisibility.IsVisibleInCampaign(character.CampaignName, effective))
                {
                    CampaignEntityVisibility.TryGetInvisibilityReason(character, effective, out var reason);
                    return new ToolResult<CombatEncounterView>(false, Error: "InvalidInput",
                        Summary: $"Combatant '{id}' is not available in campaign '{effective}'. {reason}");
                }

                if (character.CurrentHp > 0)
                {
                    validCharacters.Add(character);
                }
                else
                {
                    droppedForZeroHp.Add(id);
                }
            }

            if (validCharacters.Count == 0)
            {
                return new ToolResult<CombatEncounterView>(false, Error: "InvalidInput",
                    Summary: "None of the specified combatants are valid and alive.");
            }

            var module = await GetActiveModuleAsync(session, effective);

            var combatants = new List<CombatantState>();
            foreach (var character in validCharacters)
            {
                var initiative = await module.Combat.RollInitiativeAsync(character);
                var budget = module.Combat.GetTurnActionBudget(character);
                combatants.Add(new CombatantState
                {
                    CharacterId = character.Id,
                    Initiative = initiative,
                    HasActedThisRound = false,
                    ActionBudget = new Dictionary<string, int>(budget),
                    ReactionAvailable = true
                });
            }

            // Sort by highest initiative first
            combatants = combatants.OrderByDescending(c => c.Initiative).ToList();

            var encounter = existing ?? new CombatEncounter { Id = combatId };
            encounter.LocationId = locationId;
            encounter.Round = 1;
            encounter.Combatants = combatants;
            var heldElsewhere = await FindExclusivelyHeldAsync(session, effective);
            encounter.ActiveTurnId = combatants.FirstOrDefault(c => !heldElsewhere.ContainsKey(c.CharacterId))?.CharacterId;
            encounter.IsActive = true;

            await session.StoreAsync(encounter, encounter.Id);

            var summary = $"Combat started at {locationId} with {combatants.Count} combatants.";
            if (droppedForZeroHp.Count > 0)
            {
                summary += $" Dropped {droppedForZeroHp.Count} combatant(s) with 0 or negative HP: {string.Join(", ", droppedForZeroHp)}.";
            }

            List<(string, object?)> events = [];
            if (abandonedRound is { } oldRound)
            {
                // overwriteActive abandons a live encounter: close it for subscribers before the new one starts.
                events.Add((CoreEvents.CombatEnded, new Dictionary<string, object?>
                {
                    [CoreEvents.Fields.EncounterId] = encounter.Id,
                    [CoreEvents.Fields.Round] = oldRound,
                    [CoreEvents.Fields.Reason] = "overwritten"
                }));
            }

            events.AddRange(
            [
                (CoreEvents.CombatStarted, new Dictionary<string, object?>
                {
                    [CoreEvents.Fields.EncounterId] = encounter.Id,
                    [CoreEvents.Fields.LocationId] = encounter.LocationId,
                    [CoreEvents.Fields.CombatantIds] = combatants.Select(c => c.CharacterId).ToList(),
                    [CoreEvents.Fields.Round] = encounter.Round
                })
            ]);
            if (encounter.ActiveTurnId != null)
            {
                events.Add(TurnStarted(encounter, newRound: true));
            }

            return await PublishCombatEventsAsync(session, effective, events, validCharacters, encounter, summary);
        });
    }

    internal Task<ToolResult<CombatEncounterView>> NextTurn(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName,
        [Description(
            "Optional. If provided, the command will fail if the current active turn does not match this ID. Helps prevent accidental double-advancing.")]
        string? expectedActiveTurnId = null)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var combatId = _keys.CombatCurrent(effective);
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounterView>(false, Error: "NotFound",
                    Summary: "No active combat encounter.");
            }

            if (!string.IsNullOrWhiteSpace(expectedActiveTurnId) && encounter.ActiveTurnId != expectedActiveTurnId)
            {
                return new ToolResult<CombatEncounterView>(false, Error: "StateDrift",
                    Summary:
                    $"Expected active turn to be '{expectedActiveTurnId}' but it was '{encounter.ActiveTurnId}'. The combat state has drifted.");
            }

            var module = await GetActiveModuleAsync(session, effective);

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Ensure all loaded characters have upgraded SystemStats before resolving their actions.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, characters, effective, _keys, traitsUpgraders: _traitsUpgraders, logger: _logger);

            // Mark current actor as having acted
            var current = encounter.Combatants.FirstOrDefault(c => c.CharacterId == encounter.ActiveTurnId);
            if (current != null)
            {
                current.HasActedThisRound = true;
            }

            var expiredMessages = new List<string>();

            // A combatant held by an Exclusive interaction mode (astral projection, ...) acts only there: skip
            // its combat turn. It stays in the encounter and can still be targeted.
            var heldElsewhere = await FindExclusivelyHeldAsync(session, effective);

            // Find next who hasn't acted and is alive
            CombatantState? GetNextAliveUnacted() => encounter.Combatants.FirstOrDefault(c =>
                !c.HasActedThisRound &&
                !heldElsewhere.ContainsKey(c.CharacterId) &&
                characters.TryGetValue(c.CharacterId, out var character) && character != null &&
                character.CurrentHp > 0);

            var next = GetNextAliveUnacted();
            var startedNewRound = false;

            if (next == null)
            {
                // Verify if anyone is actually alive
                if (!encounter.Combatants.Any(c =>
                        characters.TryGetValue(c.CharacterId, out var character) && character != null &&
                        character.CurrentHp > 0))
                {
                    encounter.IsActive = false;
                    encounter.ActiveTurnId = null;
                    await session.StoreAsync(encounter, encounter.Id);
                    // Reactions are best-effort here: the encounter is over either way, so a FailCommit fault
                    // does not keep a combat of corpses alive. Faults still show in the log and summary.
                    await _repository.PublishEventsAsync(new CampaignSession(session, effective),
                        [CombatEnded(encounter, "no_combatants_standing")], characters.Values.Where(c => c != null)!, encounter);
                    await session.SaveChangesAsync();
                    return new ToolResult<CombatEncounterView>(false, CombatEncounterView.From(encounter),
                        Error: "CombatEnded",
                        Summary: "No valid and alive combatants remain. Combat has ended or cannot proceed.");
                }

                // New round
                startedNewRound = true;
                encounter.Round++;
                foreach (var c in encounter.Combatants)
                {
                    c.HasActedThisRound = false;
                    c.ReactionAvailable = true;
                }
                next = GetNextAliveUnacted(); // Retrieve the first alive person again

                // Expire round-based status effects
                foreach (var character in characters.Values.Where(c => c != null))
                {
                    if (character.SystemStats?.StatusEffects != null)
                    {
                        var effects = character.SystemStats.StatusEffects;
                        var toRemove = effects.Where(e =>
                            e.ExpiresAtRound.HasValue && e.ExpiresAtRound.Value <= encounter.Round).ToList();
                        foreach (var effect in toRemove)
                        {
                            effects.Remove(effect);
                            expiredMessages.Add($"Expired effect '{effect.Name}' on '{character.Name}'.");
                        }
                    }
                }
            }

            encounter.ActiveTurnId = next?.CharacterId;

            // Refresh action budget for the new active combatant
            if (next != null && characters.TryGetValue(next.CharacterId, out var nextCharacter) && nextCharacter != null)
            {
                var freshBudget = module.Combat.GetTurnActionBudget(nextCharacter);
                next.ActionBudget = new Dictionary<string, int>(freshBudget);
            }
            await session.StoreAsync(encounter, encounter.Id);

            var summary = encounter.ActiveTurnId is null
                ? $"No combatant can act this round (Round {encounter.Round}): everyone standing is held by an exclusive mode."
                : $"Advanced to turn of {encounter.ActiveTurnId} (Round {encounter.Round}).";
            var skipped = heldElsewhere
                .Where(h => encounter.Combatants.Any(c => string.Equals(c.CharacterId, h.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (skipped.Count > 0)
            {
                summary += " Skipped (held by an exclusive mode): " +
                           string.Join(", ", skipped.Select(h => $"{h.Key} in '{h.Value}'")) + ".";
            }
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            if (encounter.ActiveTurnId is null)
            {
                return new ToolResult<CombatEncounterView>(true, CombatEncounterView.From(encounter), summary);
            }

            return await PublishCombatEventsAsync(session, effective,
                [TurnStarted(encounter, newRound: startedNewRound)], characters.Values.Where(c => c != null)!, encounter, summary);
        });
    }

    internal Task<ToolResult<CombatEncounterView>> EndCombat(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var combatId = _keys.CombatCurrent(effective);
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounterView>(false, Error: "NotFound",
                    Summary: "No active combat encounter to end.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Ensure all loaded characters have upgraded SystemStats before processing end-of-combat effects.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, characters, effective, _keys, traitsUpgraders: _traitsUpgraders, logger: _logger);

            var expiredMessages = new List<string>();

            // Clear all round-based status effects when combat ends.
            // This implements "until end of combat" semantics for effects created with ExpiresAtRound.
            // Day-based effects (ExpiresAtDay) are handled separately by StatusExpiryRule during advance_world.
            // Note: This is intentionally aggressive — all round-tied effects are removed on combat end.
            foreach (var character in characters.Values.Where(c => c != null))
            {
                if (character.SystemStats?.StatusEffects != null)
                {
                    var effects = character.SystemStats.StatusEffects;
                    var toRemove = effects.Where(e => e.ExpiresAtRound.HasValue).ToList();
                    foreach (var effect in toRemove)
                    {
                        effects.Remove(effect);
                        expiredMessages.Add($"Cleared effect '{effect.Name}' on '{character.Name}'.");
                    }
                }

                // Recover pools with RecoveryType.EncounterEnd
                if (character.SystemStats?.ResourcePools != null)
                {
                    foreach (var poolEntry in character.SystemStats.ResourcePools)
                    {
                        var pool = poolEntry.Value;
                        if (pool.Recovery == RecoveryType.EncounterEnd && pool.Current < pool.Max)
                        {
                            pool.Current = pool.Max;
                            expiredMessages.Add($"Recovered {character.Name}'s {poolEntry.Key} to {pool.Max}.");
                        }
                    }
                }
            }

            encounter.IsActive = false;
            encounter.ActiveTurnId = null;

            await session.StoreAsync(encounter, encounter.Id);

            var summary = "Combat encounter ended.";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return await PublishCombatEventsAsync(session, effective, [CombatEnded(encounter, "ended")],
                characters.Values.Where(c => c != null)!, encounter, summary);
        });
    }

    private static (string, object?) TurnStarted(CombatEncounter encounter, bool newRound) =>
        (CoreEvents.CombatTurnStarted, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.EncounterId] = encounter.Id,
            [CoreEvents.Fields.Round] = encounter.Round,
            [CoreEvents.Fields.CharacterId] = encounter.ActiveTurnId,
            [CoreEvents.Fields.NewRound] = newRound
        });

    private static (string, object?) CombatEnded(CombatEncounter encounter, string reason) =>
        (CoreEvents.CombatEnded, new Dictionary<string, object?>
        {
            [CoreEvents.Fields.EncounterId] = encounter.Id,
            [CoreEvents.Fields.Round] = encounter.Round,
            [CoreEvents.Fields.Reason] = reason
        });

    /// <summary>
    /// Publishes combat lifecycle events and folds the outcome into the tool result: reaction messages and
    /// fault lines join the summary; a FailCommit fault fails the call, so nothing is saved.
    /// </summary>
    private async Task<ToolResult<CombatEncounterView>> PublishCombatEventsAsync(
        IAsyncDocumentSession session,
        string effective,
        IReadOnlyList<(string Topic, object? Data)> events,
        IEnumerable<Character> characters,
        CombatEncounter encounter,
        string summary)
    {
        var published = await _repository.PublishEventsAsync(new CampaignSession(session, effective), events, characters, encounter);
        if (published.Summary.Count > 0)
        {
            summary += " " + string.Join(" ", published.Summary);
        }

        return published.Success
            ? new ToolResult<CombatEncounterView>(true, CombatEncounterView.From(encounter), summary)
            : new ToolResult<CombatEncounterView>(false, Error: "PluginFault",
                Summary: "NOT SAVED: a plugin reaction that must succeed failed. " + summary);
    }

    /// <summary>Character id → mode id, for every participant of an active mode whose claim is Exclusive.</summary>
    private async Task<Dictionary<string, string>> FindExclusivelyHeldAsync(IAsyncDocumentSession session, string effective)
    {
        var held = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_modeSelector is null)
        {
            return held;
        }

        var config = await session.LoadAsync<CampaignConfig>(_keys.Config(effective));
        foreach (var modeId in config?.EnabledModeIds ?? [])
        {
            if (_modeSelector.TryGetMode(modeId)?.ParticipantClaim != ModeParticipantClaim.Exclusive)
            {
                continue;
            }

            var mode = await session.LoadAsync<ModeEncounter>(_keys.ModeCurrent(effective, modeId));
            if (mode is not { IsActive: true })
            {
                continue;
            }

            foreach (var participant in mode.Participants)
            {
                held.TryAdd(participant.CharacterId, modeId);
            }
        }

        return held;
    }

    internal Task<ToolResult<object>> GetCombat(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var encounter = await _repository.GetActiveCombatAsync(new CampaignSession(session, effective));
            if (encounter?.IsActive != true)
            {
                return new ToolResult<object>(true, new { Status = "No active combat." },
                    "No active combat encounter.");
            }

            return new ToolResult<object>(true, new
            {
                LocationId = encounter.LocationId,
                Round = encounter.Round,
                ActiveTurnId = encounter.ActiveTurnId,
                ParticipantCount = encounter.Combatants.Count,
            },
            $"Active combat at {encounter.LocationId}, round {encounter.Round}.");
        }, saveChanges: false);
    }

    private async Task<IRulesetModule> GetActiveModuleAsync(IAsyncDocumentSession session, string effective)
    {
        var config = await _repository.GetCampaignConfigAsync(new CampaignSession(session, effective));
        return _rulesetSelector.GetModule(config.ActiveSystem);
    }
}