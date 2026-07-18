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
    [Description(@"COMBAT TOOL: Starts a new combat encounter at the specified location.
Rolls initiative for all combatants based on the active ruleset system and establishes the turn order. If a combat is already active, it is overwritten. Requires campaignName.

Parameter name is combatantIds (not combatants). Example: start_combat(""locations/tavern"", [""chars/pc1"", ""chars/pc2"", ""chars/goblin1""])")]
    public Task<ToolResult<CombatEncounter>> StartCombat(
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
                exampleCall: "start_combat(\"locations/tavern\", [\"chars/hero\"])");
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
            var existing = await _repository.GetActiveCombatAsync(session, effective);
            if (existing?.IsActive == true && !overwriteActive)
            {
                return new ToolResult<CombatEncounter>(false,
                    Error: $"Combat already active at {existing.LocationId} (round {existing.Round}). " +
                           "Call end_combat to abandon, or pass overwriteActive:true to force restart.");
            }
            var uniqueIds = (combatantIds ?? []).Distinct().ToList();
            var loadedCharacters = await session.LoadAsync<Character>(uniqueIds);
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

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Advances the turn order to the next combatant.
If all combatants have acted, advances to the next round. Skips dead combatants (HP <= 0).
Round-based status effects naturally expire during this transition when their round duration ends.
Requires campaignName.")]
    public Task<ToolResult<CombatEncounter>> NextTurn(
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

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Ends the current active combat encounter and wraps up the state.
Aggressively clears all round-based status effects (e.g., 'until end of combat' effects) from all combatants.
Day-based effects remain active. Requires campaignName.")]
    public Task<ToolResult<CombatEncounter>> EndCombat(
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

    [ToolCategory("Combat & rulesets")]
    [McpServerTool(UseStructuredContent = true)]
    [Description("COMBAT TOOL: Retrieve the active combat encounter (if any), regardless of location. Returns null if no active combat.")]
    public Task<ToolResult<object>> GetCombat(
        [Description(ToolParameterDescriptions.CampaignNameRequired)] string campaignName)
    {
        return ExecuteForCampaignAsync(campaignName, async (effective, session) =>
        {
            var encounter = await _repository.GetActiveCombatAsync(session, effective);
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
        var config = await _repository.GetCampaignConfigAsync(session, effective);
        return _rulesetSelector.GetModule(config.ActiveSystem);
    }
}