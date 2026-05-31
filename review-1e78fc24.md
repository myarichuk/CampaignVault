# Code Review: CampaignVault Combat & Ruleset Overhaul (commit range master @ 16 ahead of origin/master, merge-base 533db87b)

## Summary

The changes introduce a multi-ruleset architecture (D&D 5e, PF2e, Fallout 2d20) with polymorphic `SystemExtension` + three `IRulesetResolver` implementations, a `RulesetAction` WorldChange type dispatched via `RulesetActionHandler`, deterministic dice via `IRollService`/`DefaultRollService`, and a simple global `CombatEncounter` ("combat/current") model with initiative rolling, turn advancement (NextTurn), and `ActiveCombat` exposure in `get_scene`/`SceneView`. Combat damage flows through `RulesetAction` -> resolver math -> nested `HpChange` mutations. The implementation is mostly correct for the happy paths of the supported action types (Attack, SkillCheck, limited ContestedCheck), with good use of JSON polymorphism, preloading in the dispatcher, and separation of rules math from LLM. Dominant risk areas: (1) missing input validation on string parameters leading to runtime exceptions, (2) absent mechanical integration of `StatusEffect.StatModifiers` into resolvers and no auto-expiry for `ExpiresAtRound` despite documentation, (3) weak combat state robustness (no dead-actor filtering, empty combatant lists, no mid-combat edits, single global combat only), (4) incomplete test coverage for the new combat tools, handler, edge cases, and cross-ruleset consistency, and (5) duplication and tight coupling between resolvers and specific extension types.

Overall the core abstractions hold and dice/crit/degree logic is sound where implemented, but the combat system feels like "Phase 1 scaffolding" rather than a fully integrated, production-hardened feature. Several correctness and maintainability issues should be addressed before heavy LLM-driven usage in mixed-ruleset campaigns.

## Issues

### Issue 1 -- Severity: bug
- File: src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:103
- Description: Unsafe `int.Parse(b)` (and similar for damageBonus) on `action.Parameters` values with no TryParse or validation. Identical pattern in ResolveAttack (ac uses TryParse but bonus does not), ResolveSkillCheck (only dc safe), ResolveContestedCheck, and mirrored in Pf2eRulesetResolver.cs:104 and Fallout2d20RulesetResolver.cs:61/101/111 etc. LLM-supplied "bonus":"foo" or malformed "dc" will throw unhandled exception during RulesetActionHandler.ApplyAsync -> resolver, surfacing as generic commit failure instead of graceful narrative error.
- Suggestion: Replace all `int.Parse(...)` with `int.TryParse(..., out var v) ? v : defaultOrErrorValue`, returning an error narrative (and no mutations) on failure, consistent with existing "Error: ..." returns for missing actor/target/dc. Add unit tests exercising bad parameter values.
- Status: open

### Issue 2 -- Severity: bug
- File: src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:29 (and symmetric in Pf2e:32, Fallout:24)
- Description: `actorStats = actor.SystemStats as Dnd5eExtension ?? new Dnd5eExtension()` (and targets) silently falls back to defaults when the active ruleset in CampaignConfig does not match the character's persisted SystemStats type (or when SystemStats is base). This allows cross-ruleset actions (e.g. D&D attack on a Fallout character) to "succeed" with zeroed stats instead of explicit mismatch error. No validation of character.SystemStats against active system.
- Suggestion: After cast, if the concrete type does not match the resolver's expected extension (or if it is still the base SystemExtension), return a clear error narrative: "Character uses incompatible ruleset stats for current ActiveSystem". Consider adding a small helper or requiring characters declare their system.
- Status: open

### Issue 3 -- Severity: bug
- File: src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:114 (similar batch[0]/[1] in contested:191)
- Description: `var outcomes = await ...RollBatchAsync...; var attackRoll = outcomes[0]; var damageRoll = outcomes[1];` performs unchecked indexing. While current call sites always supply exactly two requests, any future change to batch construction or a fake/mock returning fewer items will throw IndexOutOfRangeException instead of a handled error. PF2e/Fallout use single RollAsync (safer).
- Suggestion: Use `.ElementAtOrDefault(0)` + null/empty check, or restructure to always use named single rolls or a result record. Add defensive guard + error narrative.
- Status: open

### Issue 4 -- Severity: bug
- File: src/CampaignVault/Tools/CampaignTools.cs:436 (StartCombat), 474 and 480 (NextTurn)
- Description: Combat tools accept and operate on empty `combatantIds` arrays (or lists that become empty), producing `CombatEncounter` with `Combatants = []`, `ActiveTurnId = null`, `IsActive = true`. NextTurn on such a state (or after all actors removed somehow) sets `ActiveTurnId = null` and can increment rounds indefinitely with no actors. No validation, no guard in get_scene filter. Also accepts duplicate IDs and non-existent character IDs (resolvers return init=0 for missing).
- Suggestion: In StartCombat: reject if `combatantIds.Length == 0`, dedupe, and verify each id loads a real Character with MaxHp > 0 before rolling/adding. In NextTurn: after computing next, if (next == null || combatants.Count == 0) return failure narrative instead of storing invalid state. Expose "remove combatant" or "replace combatant" WorldChange/Meta action.
- Status: open

### Issue 5 -- Severity: bug
- File: src/CampaignVault/Tools/CampaignTools.cs:467 (NextTurn) and throughout combat flow; also Models/CombatEncounter.cs
- Description: No concept of "dead" or "incapacitated" actors. NextTurn and StartCombat will happily give turns to, and allow attacks targeting, characters with `CurrentHp <= 0`. Resolvers and HpChangeHandler will still apply further negative deltas (clamped at 0) and generate narratives. No automatic removal from `Combatants` list on death, no skip logic, no morale/flee hooks. CombatEncounter has no "removed" or "conditions" per combatant.
- Suggestion: Either (a) filter `!HasActedThisRound && (char.CurrentHp > 0)` when selecting next (requiring character preload in tools), or (b) introduce CombatantState flags (IsDead, IsRemoved) mutated by HpChangeHandler or a new death handler when HP hits 0 during combat. At minimum document the current "zombie combatants" behavior and add tests.
- Status: open

### Issue 6 -- Severity: bug
- File: src/CampaignVault/Models/StatusEffect.cs:69 (and docs in WorldChanges.cs:69, StatusChangeHandler.cs:17)
- Description: `ExpiresAtRound` (and day expiry) is fully documented as auto-enforced by "AdvanceWorldAsync / CombatEncounter advancement", but no code anywhere implements filtering or removal of expired `SystemStats.StatusEffects` (searches in CampaignRepository.cs, DefaultSimulationEngine.cs, ScheduleEvaluationRule, NextTurn/StartCombat/EndCombat, etc. return zero hits). Combat round advancement does not touch status lists.
- Suggestion: Implement expiry pass (e.g. in NextTurn after round++, or a dedicated ISimulationRule, or inside StatusChangeHandler on load) that removes effects where (ExpiresAtRound != null && effect.ExpiresAtRound <= currentEncounter.Round) or equivalent for days. Update tests and remove aspirational comments until implemented. Consider surfacing expired effects in commit summaries.
- Status: open

### Issue 7 -- Severity: bug
- File: src/CampaignVault/Rulesets/*RulesetResolver.cs (all three Resolve* methods, e.g. Dnd5e:154, Pf2e:134, Fallout:66)
- Description: None of the resolvers consult `character.SystemStats.StatusEffects` or apply any `StatModifiers` when computing bonuses, AC, target numbers, or damage. StatusEffects are stored and round-tripped through get_scene / NpcPresenceSummary, but provide zero mechanical effect on attacks, checks, or initiative. This makes "Frightened", "Mangled Hand (-2 AttackRoll)", etc. purely narrative.
- Suggestion: Add a protected helper in a common base resolver (or extension methods on SystemExtension) that folds status modifiers (and perhaps Willpower/Morale/Fatigue) into the final bonus/ TN / AC before rolling. Update all GetSkillOrAbilityBonus, attackBonus, etc. call sites. This is required for the combat feature to be credible.
- Status: open

### Issue 8 -- Severity: suggestion
- File: src/CampaignVault/Tools/CampaignTools.cs:117 (Commit tool description)
- Description: The `[Description]` for the `commit` tool hard-codes the supported `$type` list and omits `"ruleset_action"` (and the new combat-related patterns). Examples also predate ruleset actions. LLM agents following the tool description literally will not discover or correctly format RulesetAction payloads.
- Suggestion: Update the description string to include `ruleset_action` (with a short usage example for Attack/SkillCheck), and point to `get_config` for the active system. Keep the list in sync with WorldChanges.cs polymorphic attributes. Consider extracting a constant or generating help text.
- Status: open

### Issue 9 -- Severity: suggestion
- File: src/CampaignVault/Rulesets/Dnd5eRulesetResolver.cs:85 (ResolveAttackAsync), Pf2e:93, Fallout:93 and parallel methods in each
- Description: Significant code duplication across the three resolvers for actor/target lookup, error narratives, parameter extraction for common attack fields (bonus, damageDice, ac/difficulty), and mutation emission. Each has its own Get*Bonus/GetAttribute helper with similar switch logic. Makes adding a fourth system or a shared "resolve basic attack" primitive expensive and error-prone.
- Suggestion: Introduce an abstract base class `RulesetResolverBase` (or a set of small strategy helpers / record types for "AttackParams") containing the shared lookup + error helpers and the common Resolve* skeleton. Keep only the system-specific math (crit handling, degree calc, pool vs d20, damage type/DR) in the concrete classes. Move common parameter keys to constants.
- Status: open

### Issue 10 -- Severity: suggestion
- File: tests/CampaignVault.Tests/Dnd5eRulesetResolverTests.cs (and sibling *ResolverTests.cs files); absence in CampaignRepositoryTests.cs and SimulationHarness/
- Description: Resolver unit tests are narrow (4 facts for D&D, 3 for PF2e, 2 for Fallout). No coverage of: ContestedCheck (D&D only), advantage/disadvantage parameters and mechanic selection, error paths (bad actor/target, missing dc, parse failures), initiative rolling, RulesetActionHandler integration, empty/missing combatants, cross-system mismatch, or full round-trip via Commit + get_scene. Zero tests exercise StartCombat/NextTurn/EndCombat tools or combat state transitions. Simulation harness and dispatcher tests ignore the new types.
- Suggestion: Add comprehensive tests: (a) dedicated CombatToolsTests or expand repo tests with in-memory session exercising full Start/Next/End + ruleset actions + assertions on stored CombatEncounter and resulting HP; (b) more resolver cases for every documented parameter and all ActionTypes (even the "not implemented" ones); (c) property-based or seeded tests for crit/degree/success-count edge margins and nat 1/20; (d) concurrency/duplicate combatant tests. Aim for >80% coverage on new files before claiming the overhaul complete.
- Status: open

### Issue 11 -- Severity: nit
- File: src/CampaignVault/Rulesets/Pf2eRulesetResolver.cs:120 (and Dnd5e crit handling)
- Description: PF2e critical damage does `finalDamage *= 2` on the post-bonus total (so bonus is doubled). D&D 5e path adds a second pure-dice roll (bonus applied only once). Both are common house rules/variants, but the inconsistency is undocumented and the PF2e comment claims "typically double the final calculated damage" without referencing actual PF2e CRB (which doubles damage dice then adds modifiers once). Fallout combat dice are separate.
- Suggestion: Document the exact crit math chosen for each system (and any house-rule SystemOptions that could alter it). If strict fidelity is desired, adjust PF2e to double only the dice portion before adding bonus, or make both configurable.
- Status: open

### Issue 12 -- Severity: suggestion
- File: src/CampaignVault/Tools/CampaignTools.cs:418 (StartCombat) and Rulesets/*RulesetResolver.cs:202 (RollInitiativeAsync)
- Description: Initiative rolling (and static calculation for Fallout) happens inside the resolver via fresh `session.LoadAsync<Character>` calls for every combatant, bypassing the pre-loaded context that RulesetAction enjoys. StartCombat therefore performs N+1 loads even though the caller already has the IDs. No caching or batch load. For large fights this is minor but noticeable.
- Suggestion: Change RollInitiativeAsync signature (or add an overload) to accept a pre-fetched Character or the stats directly, and have StartCombat pre-load the combatants the same way WorldChangeDispatcher does for RulesetAction. Keep the session-based version only for direct tool use.
- Status: open

### Issue 13 -- Severity: nit
- File: src/CampaignVault/Rulesets/Fallout2d20RulesetResolver.cs:147 (RollInitiativeAsync)
- Description: Initiative for Fallout returns a pure `stats.Perception + stats.Agility` (no dice roll, no variance, no use of Luck or other). Other systems always involve at least 1d20 + mod. Ties will be resolved purely by LINQ OrderByDescending stability (input order after equal values).
- Suggestion: Either document "Fallout uses static initiative = Per + Agi (per house rule or simplified)" or implement a simple 1d10 + (Per+Agi)/2 or similar 2d20-derived roll for consistency with the system's dice philosophy. Consider exposing "initiativeSkill" or "initiativeAttribute" parameters (already hinted in WorldChanges.cs docs) uniformly.
- Status: open

### Issue 14 -- Severity: suggestion
- File: src/CampaignVault/Data/ChangeHandlers/WorldChangeDispatcher.cs:109 and Models/WorldChanges.cs:246
- Description: RulesetAction preloading and handling is present, but the single global "combat/current" CombatEncounter document is never pre-loaded or protected by the dispatcher in the same way characters are. A concurrent StartCombat + NextTurn (possible under HTTP transport or parallel MCP calls) can race on the same document ID with only Raven's optimistic concurrency as protection (caught at tool level).
- Suggestion: Add CombatEncounter to the pre-load logic in DispatchAsync when a RulesetAction or future Combat-specific change is seen (or always load it when present). Consider adding a small version/turn counter or using a "combat lock" pattern / conditional Store for turn advancement. Document the single-combat-at-a-time global assumption.
- Status: open

### Issue 15 -- Severity: nit
- File: src/CampaignVault/Rulesets/IRulesetResolverSelector.cs:21 and Program.cs:60
- Description: Selector does a linear `FirstOrDefault` over the registered resolvers on every RulesetAction and every StartCombat. With only three items this is negligible, but the throw message uses the enum.ToString which will be unhelpful if a fourth system is added without a resolver registration.
- Suggestion: Change to a Dictionary<RulesetSystem, IRulesetResolver> built once in the selector ctor (or use a keyed service in DI). Validate at startup that every RulesetSystem value has exactly one resolver.
- Status: open

## Additional Observations (non-issue)

- The polymorphic JSON design for WorldChange + SystemExtension is clean and works well (see RulesetSerializationTests).
- Dice service correctly implements the advertised mechanics; advantage pool comparison and success-count + complication logic are deterministic and testable.
- No unnecessary clones, locks, or async void in the new code paths.
- Performance for typical TTRPG party sizes (4-8) is fine; the O(n) scans in NextTurn and single-doc combat model scale acceptably.
- The decision to keep combat manual (tools only) rather than tying it into the day-based simulation engine is architecturally sound.

End of review. All issues marked open for developer triage.