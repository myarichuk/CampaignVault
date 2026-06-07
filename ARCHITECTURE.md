# Campaign Vault Architecture

## Entity Scoping & Multi-Campaign Design

The Campaign Vault was originally designed for a single global campaign. In Phase 0, it was refactored to support multiple discrete campaigns within the same database instance. However, to keep the system robust and to respect existing client data, the scoping policy treats different data tiers differently.

### 1. Per-Campaign Singletons (Fully Namespaced)
Certain data is highly specific to a particular campaign instance and represents the "current state" of that world simulation. These are stored as strictly namespaced singletons via `CampaignDocumentKeys`.

- **Campaign (Meta)**: `campaigns/{name}/meta`
- **CampaignConfig**: `campaigns/{name}/config` (Holds active ruleset system and house rules)
- **CampaignTime**: `campaigns/{name}/time`
- **CombatEncounter**: `campaigns/{name}/combat/current`
- **NeedDescriptorsConfig**: `campaigns/{name}/need-descriptors`

**Why?** These documents dictate the rules and the global clock of a given campaign instance. They must never cross-contaminate.

### 2. ID-Controlled World Entities (Globally Flat)
World entities (Locations, Characters, Items, Lore, Rumors, Events) are stored in a flat namespace and are globally queryable, although queries and lookups can (and often do) accept a `campaignName` for future-proofing. Currently, they rely on the **caller (or LLM) ensuring unique IDs** to provide isolation.

For example, a character created as `characters/bram-ironarm` in Campaign A is technically visible to Campaign B if Campaign B knows the ID or if it appears in a raw search.

**Why?**
1. **Backward Compatibility**: Migrating thousands of existing records (like `characters/grog`) to `campaigns/{name}/characters/grog` would have broken many established games and required complex database migration scripts.
2. **Shared Universes**: In the future, a flat entity namespace allows players to run multiple campaigns in the *same* world (e.g. West Marches style), or have crossover NPCs. The campaign-specific state (like Time and Combat) remains isolated, while the Lore and Locations can be shared.
3. **Simplicity**: The MCP surface is already complex. Forcing the LLM to prepend campaign slugs to every single ID during combat or relationship updates introduces high risk of hallucinated keys.

If strict entity isolation is required later, we can introduce a `CampaignId` property on these documents and filter them at the repository query level, rather than changing their document IDs.

**Update (scoping hardening):** As of focused hardening (per code_review.md), entities now have `CampaignName` set on create/upsert via context. Key paths (GetCharacterPressureAsync, Advance sim queries, Query* methods, GetScene, handlers) post-filter (loose for shareable entities like chars/locs per design, strict for campaign-specific like events/rumors). Legacy nulls still visible for test compat, but no play data requires BC. See plan.md for details.

### 3. Simulation Scope
The `AdvanceWorldAsync` simulation currently operates on a global scope for entities (it evaluates all characters with a schedule). However, it passes the `effective` campaign context down into the simulation loop so rules can (in the future) filter their evaluation scope based on the active campaign. Because time is strictly isolated per campaign, the time-decay mechanics correctly apply only to the running campaign.

## MCP Context & Session State

Because CampaignVault is hosted as a Model Context Protocol (MCP) server utilizing Server-Sent Events (SSE) and HTTP POSTs, standard ASP.NET Core `AsyncLocal` contexts will tear down between individual tool invocations. 
To preserve context (like the currently selected campaign) across the logical "session" of an LLM client, `ICurrentCampaignContext` is implemented as a long-lived Singleton with standard backing fields, preventing context loss on sequential tool calls. Furthermore, data-access methods enforce optimistic concurrency with built-in retry loops inside the `CampaignTools` wrapper to seamlessly recover from transient `StateDriftConflict` errors on rapidly successive edits (common during heavy simulation phases).

## Environmental & Economic Simulation

Phase 8 expanded the simulation depth to include physics, economics, and logistics, handled by deterministic engine rules rather than LLM guesswork:
- **Location State & Visuals:** Locations possess `CurrentState` (e.g., "burning", "flooded"), `DistinctiveFeatures`, and `VisualTags`. Changes to these properties are managed by `LocationUpdateHandler` and directly inform the LLM's `GetScene` context, ensuring narrative fidelity to mechanical conditions.
- **Faction Economics:** Factions operate on supply and demand through `EconomicDemand` dictionaries. The `FactionEcosystemRule` simulates economic decay/recovery and resource consumption based on time cycles and current faction stances (e.g., going to war increases demand for weapons/armor).
- **Party Intercept:** The core loop identifies when the party is carrying items that factions demand and emits actionable `WorldPressure` (e.g., "The party is carrying highly sought-after Spell Scrolls").

## Ruleset Architecture (Future/In-Progress)

CampaignVault uses a pluggable ruleset system to ensure mechanical determinism (advantage, dice pools, degrees of success) while avoiding hard-coded 5e logic in the core engine. 
Future architectural milestones will formalize this into focused interfaces:
- `ICombatRuleset`, `IActionResolution`, `IStatusApplication`, `IInitiative`
- `ruleset_action` will dispatch directly into the current campaign's selected ruleset implementation for validation and mechanical execution, returning the outcome.
- Rulesets will be able to inject their own `WorldPressure` items directly into `GetWorldState` or `GetScene` (e.g., a specific Ruleset detecting a poisoned condition and contributing "This character has a condition that should be resolved this round").
