using System.ComponentModel;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CombatTools : CampaignToolBase, IMcpServerTool
{
    private readonly IRulesetModuleSelector _rulesetSelector;

    public CombatTools(
        CampaignRepository repository,
        CampaignDocumentKeys keys,
        IRulesetModuleSelector rulesetSelector,
        ILogger<CombatTools>? logger = null)
        : base(repository, keys, logger)
    {
        _rulesetSelector = rulesetSelector;
    }

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT CONTROL: Single tool for the combat lifecycle, dispatched by 'action':
- action:'start' — begin an encounter: requires locationId + combatantIds (array of character IDs); rolls initiative via the active ruleset and establishes turn order. Pass overwriteActive:true to abandon a stuck encounter and restart.
- action:'next' — advance to the next combatant (new round when everyone has acted; expires round-based effects; skips dead combatants). Optional expectedActiveTurnId guards against double-advancing.
- action:'end' — end the encounter: clears round-based status effects, recovers encounter-end resource pools.
- action:'status' — read-only: the active encounter (location, round, whose turn), or 'no active combat'.
Combat ACTIONS (attacks, spells, checks) are NOT here — commit them via take_turn with a ruleset_action change; the engine enforces turn order and action economy against this encounter. Requires campaignName.")]
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

    internal Task<ToolResult<CombatEncounter>> StartCombat(
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
            return ToolArgumentErrors.Missing<CombatEncounter>(
                "locationId",
                "Pass where combat occurs.",
                exampleCall: "combat(action: \"start\", locationId: \"locations/tavern\", combatantIds: [\"chars/hero\"])");
        }

        if (combatantIds?.Count == 0)
        {
            return Task.FromResult(new ToolResult<CombatEncounter>(
                false,
                Error: "InvalidInput",
                Summary: "Cannot start combat with zero combatants. Pass combatantIds (not combatants) — an array of character IDs, e.g. [\"chars/valen\", \"chars/guard\"]."));
        }

        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var existing = await _repository.GetActiveCombatAsync(new CampaignSession(session, effective));
            if (existing?.IsActive == true && !overwriteActive)
            {
                return new ToolResult<CombatEncounter>(false,
                    Error: $"Combat already active at {existing.LocationId} (round {existing.Round}). " +
                           "Call combat(action: 'end') to abandon, or pass overwriteActive:true to force restart.");
            }
            var uniqueIds = (combatantIds ?? []).Distinct().ToList();
            var loadedCharacters = await session.LoadAsync<Character>(uniqueIds);

            // Ensure all loaded characters have upgraded SystemStats (type coercion + SkillModifiers derivation).
            // Initiative rolls and other combat resolution need SkillModifiers to be populated.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, loadedCharacters, effective, _keys);

            var validCharacters = new List<Character>();
            var droppedForZeroHp = new List<string>();

            foreach (var id in uniqueIds)
            {
                if (!loadedCharacters.TryGetValue(id, out var character) || character is null)
                {
                    return new ToolResult<CombatEncounter>(false, Error: "NotFound",
                        Summary: $"Character '{id}' not found.");
                }

                if (!CampaignEntityVisibility.IsVisibleInCampaign(character.CampaignName, effective))
                {
                    CampaignEntityVisibility.TryGetInvisibilityReason(character, effective, out var reason);
                    return new ToolResult<CombatEncounter>(false, Error: "InvalidInput",
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
                return new ToolResult<CombatEncounter>(false, Error: "InvalidInput",
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

            var encounter = new CombatEncounter
            {
                Id = _keys.CombatCurrent(effective),
                LocationId = locationId,
                Round = 1,
                Combatants = combatants,
                ActiveTurnId = combatants.FirstOrDefault()?.CharacterId,
                IsActive = true
            };

            await session.StoreAsync(encounter, encounter.Id);

            var summary = $"Combat started at {locationId} with {combatants.Count} combatants.";
            if (droppedForZeroHp.Count > 0)
            {
                summary += $" Dropped {droppedForZeroHp.Count} combatant(s) with 0 or negative HP: {string.Join(", ", droppedForZeroHp)}.";
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    internal Task<ToolResult<CombatEncounter>> NextTurn(
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
                return new ToolResult<CombatEncounter>(false, Error: "NotFound",
                    Summary: "No active combat encounter.");
            }

            if (!string.IsNullOrWhiteSpace(expectedActiveTurnId) && encounter.ActiveTurnId != expectedActiveTurnId)
            {
                return new ToolResult<CombatEncounter>(false, Error: "StateDrift",
                    Summary:
                    $"Expected active turn to be '{expectedActiveTurnId}' but it was '{encounter.ActiveTurnId}'. The combat state has drifted.");
            }

            var module = await GetActiveModuleAsync(session, effective);

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Ensure all loaded characters have upgraded SystemStats before resolving their actions.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, characters, effective, _keys);

            // Mark current actor as having acted
            var current = encounter.Combatants.FirstOrDefault(c => c.CharacterId == encounter.ActiveTurnId);
            if (current != null)
            {
                current.HasActedThisRound = true;
            }

            var expiredMessages = new List<string>();

            // Find next who hasn't acted and is alive
            CombatantState? GetNextAliveUnacted() => encounter.Combatants.FirstOrDefault(c =>
                !c.HasActedThisRound &&
                characters.TryGetValue(c.CharacterId, out var character) && character != null &&
                character.CurrentHp > 0);

            var next = GetNextAliveUnacted();

            if (next == null)
            {
                // Verify if anyone is actually alive
                if (!encounter.Combatants.Any(c =>
                        characters.TryGetValue(c.CharacterId, out var character) && character != null &&
                        character.CurrentHp > 0))
                {
                    return new ToolResult<CombatEncounter>(false, Error: "CombatEnded",
                        Summary: "No valid and alive combatants remain. Combat has ended or cannot proceed.");
                }

                // New round
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

            var summary = $"Advanced to turn of {encounter.ActiveTurnId} (Round {encounter.Round}).";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    internal Task<ToolResult<CombatEncounter>> EndCombat(
        [Description(ToolParameterDescriptions.CampaignNameRequired)]
        string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var combatId = _keys.CombatCurrent(effective);
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounter>(false, Error: "NotFound",
                    Summary: "No active combat encounter to end.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Ensure all loaded characters have upgraded SystemStats before processing end-of-combat effects.
            await SystemStatsUpgradeHelper.UpgradeCharacterSystemStatsAsync(
                session, characters, effective, _keys);

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

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
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