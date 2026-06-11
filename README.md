# D&D Campaign Vault - Living World DM Engine

A high-bandwidth Model Context Protocol (MCP) server that turns RavenDB into a persistent, reactive simulation engine for long-running D&D (or other TTRPG) campaigns. It is purpose-built as an MCP to solve the challenges of state tracking, context limits, and hallucination when an LLM performs the DM role over long campaigns, providing reliable world state tracking, NPC psychology, rumor lifecycles, and atomic narrative resolution across many sessions.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC tiredness, and surface aging unresolved events as pressure via `DefaultSimulationEngine` and its simulation rules.
- **Multi-System Ruleset Engine**: Full polymorphic support for **D&D 5e**, **Pathfinder 2e**, and **Fallout 2d20**. The C# MCP handles math, advantage, 4-degrees of success, and dice pools deterministically.
- **Structured Combat Encounters**: Start, advance, and resolve tactical combat with ruleset initiative rolls at `start_combat`, turn-order tracking, and HP/status mutations applied atomically via `commit`.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`). The LLM instantly receives the `ActiveCombat` state and `SystemStats` (AC, SPECIAL, etc.) for everyone present.
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: `get_world_state`, `get_scene`, and `advance_world` surface `WorldPressure`—proactive alerts about ticking clocks and background events. `get_npc_context` can also surface urgent initiative pressures.
- **Unified Search**: Keyword/wildcard search across lore, characters, and locations in one shot (`search_world`).

## Recent Updates
- **Engagement Relations & Spatial Positioning**: Pairwise scene anchors (`engagement_relation`: category + freeform verb) vs. relative placement (`spatial_position`: distance band, bearing, zone). Category defaults control travel blocks and scene pressure; ruleset resolvers auto-establish/clear grapple engagements on contested maneuver checks. See `get_help` and `ARCHITECTURE.md`.
- **Multi-Campaign Support**: Per-campaign singletons (time, combat, config) with `select_campaign`, `create_campaign`, and `set_active_system` (with system lock-in). World entities are campaign-tagged and filtered at query time; characters/locations with no `CampaignName` may still appear across campaigns (shared-universe design).
- **Ruleset Integration & Combat**: `RulesetAction` mutations, a polymorphic `SystemExtension` for stats, deterministic resolvers (D&D 5e, PF2e, Fallout 2d20), and dedicated combat turn tracking (`start_combat`, `next_turn`, `end_combat`) natively wired into `get_scene`.
- **Correctness & Reliability**: `HpChange` clamps to `MaxHp`, `AttributeChange` uses `isDelta`, and status modifiers/expiry are active.

## Core Tool Surface

### Session & exploration

| Tool | Purpose |
|------|---------|
| `get_current_campaign` | Active campaign name, ruleset, lock-in status |
| `get_world_state` | Session kickoff: time, rumors, recent events, pressures (`Data.WorldPressure`) |
| `get_scene` | Location, NPCs, items, rumors, `ActiveCombat`, `SystemStats`, pressures (`ToolResult.WorldPressure`) |
| `get_npc_context` | Deep NPC psychology, memories, initiative signals |
| `get_npc_needs` | Current needs + merged descriptors |
| `get_need_descriptors` | Per-campaign shared need descriptions |
| `search_world` | Keyword search across lore, characters, locations |
| `recall_history` | Keyword search over past event summaries |
| `get_help` | Built-in DM manual and copy-paste patterns |

### Mutation & time

| Tool | Purpose |
|------|---------|
| `commit` | Universal atomic write (`WorldChange[]` with `$type` discriminators) |
| `advance_world` | Fast-forward days, run simulation rules, return pressures |

### Combat & rulesets

| Tool | Purpose |
|------|---------|
| `get_config` / `set_active_system` | Read or set active ruleset (D&D 5e, PF2e, Fallout 2d20) |
| `start_combat` / `next_turn` / `end_combat` | Initiative at start, turn tracking, round-based status expiry |

### Campaign management

| Tool | Purpose |
|------|---------|
| `create_campaign` / `list_campaigns` / `select_campaign` | Create, list, and activate campaigns |

### Deep dives

| Tool | Purpose |
|------|---------|
| `get_faction_context` | Full faction document (stances, territory, `EconomicDemand`) |
| `get_quest_details` | Full quest document (objectives, deadlines, progress timestamps) |

**World Builder tools** (`upsert_character`, `upsert_location`, `upsert_lore`, `define_need_descriptor`): These exist for initial seeding and major structural work. During actual play, strongly prefer `commit` (especially with `activity` changes). Call `get_help` for detailed guidance and copy-paste patterns.

**Open-World Flavor, Transients & Laziness Mitigation**: The system is deliberately designed so an LLM performing the DM role can be "lazy" or exploratory without breaking the world model. Most narration (crowds, one-off details, unnamed NPCs) stays ephemeral. Only meaningful things are persisted via small `commit` payloads using `location_create` / `character_create` / `item_create` etc. 

- `get_scene` returns `PointsOfInterest` (light list) and uses `AmbientCrowd` hints for flavor without creating documents.
- The engine auto-links maps on `location_create` (supply `connectedFromLocationId`).
- Transients (created without `schedule` + `keepAlive:false`) are auto-evicted by `TransientEvictionRule` during `advance_world` when areas go "cold".
- **Critical**: `get_scene`, `get_world_state`, and `advance_world` return `WorldPressure` containing `ENGINE WARNING:` and `NARRATIVE PROMPT:` items. These include **exact copy-paste JSON** for the `commit` needed to fix hallucinations, dead-ends, empty-but-expected-crowds, broken links, etc. Treat them as mandatory directives. Call `get_help` for the full "Lazy Tavern" walkthrough and patterns.
- This directly addresses the "silly factor" of forcing perfect polymorphic JSON arrays for every flavor element the LLM narrates.

See `get_help` for the full DM manual (engagements, spatial positions, grapple patterns). See `ARCHITECTURE.md` for scoping, simulation, engagement/spatial design, and ruleset integration. See `docs/recommended-system-prompt.md` for a copy-paste LLM system prompt.

## Open-World & Sandbox Mechanics

The engine provides deep structural tracking for macro-mechanics:
- **Location Physics & Tags**: Add temporary tags (e.g., `["wet", "smoky"]`), narrative states, or distinctive features directly to Locations, Characters, and Items via `commit`. The engine will pressure you when tags impact a scene. You are the physics engine: interpret the tags and narrate accordingly!
- **Epistemic Drift & Memories**: Use `knowledge_update` to record key facts in an NPC's `Memories`. Over time, trivial and important memories will "decay", and the engine will pressure you to reflect memory loss, epistemic drift, or confusion.
- **Factions & Economy**: Track influence, wealth, and stance matrices. Background rules shift their influence over time, and factions dynamically demand resources (`EconomicDemand`). If a faction is desperate for "spell scrolls" and the party has them, `get_scene` will surface the pressure. Use `get_faction_context` to do a deep dive.
- **Quests**: Manage long-term objectives with strict state tracking (Open, InProgress, Complete, Failed). Quests decay towards deadlines as time passes, emitting `Quest:Stale` and `Quest:ApproachingDeadline` pressures so the LLM doesn't forget them. Use `get_quest_details` to pull the full quest document (objectives, deadlines, rewards, and per-objective progress timestamps).
- **Travel**: Record journeys with `$type: travel` in `commit` (applies exit distance, tiredness, time advance, and optional random encounters). Hard `engagement_relation` entries block travel until cleared. If you call `get_scene` with `partyPresent=true` but no `KeepAlive` PC is at that location, the engine raises `Location:MissingTravelCommit` with ready `travel` JSON. Interrupted en-route travel surfaces `Travel:Interrupted` pressure until you resolve the encounter and commit another `travel`.
- **Engagements & Spatial Positions**: Use `engagement_relation` for unresolved pairwise beats (grapples, hugs, tending wounds) and `spatial_position` for relative placement (e.g. drunk `Near` the party at the bar). Combat grapples are handled by `ruleset_action`; commit engagements manually for RP beats. Call `get_help` for copy-paste patterns and clearance (`verb` / `distanceBand` null).



## The Open Psychological Model (Needs, Wants, Fears)

The NPC "Mind" system is intentionally open-ended. There is no closed list of needs.

- Discover needs at runtime via `get_npc_needs`, `get_scene`, `get_npc_context`, and `get_need_descriptors`.
- Use `define_need_descriptor` to create **per-campaign** shared descriptions for custom needs. These are automatically merged into NPC views (per-NPC descriptors override).
- Freely invent narrative-appropriate needs (`wanderlust`, `duty`, `guilt`, `debt_pressure`, etc.) and provide human-readable `NeedDescriptors`.
- For initial world building, the `upsert_*` tools exist. In practice, many users find `commit` (with rich `EventOccurred` + `RelationshipChange` + `ActivityChange` + `NeedChange`) to be the more reliable way to evolve the world during play.

Richly seed key NPCs early with deep `Mind` data (Wants/Fears/Knows, custom needs + descriptors, Schedule + Routines, equipment via Items). The simulation and behavioral synthesis will make much better use of that data than shallow characters.

**Shared Need Descriptors**: Use `define_need_descriptor` to create reusable descriptions for custom needs (e.g. "homesickness") within the active campaign. They are stored at `campaigns/{name}/config/need-descriptors` and automatically appear (merged) in `get_npc_needs`, `get_npc_context`, and `get_scene`. Use `get_need_descriptors` to list what is defined for the campaign.



## Deployment to Fly.io

### 1. Create the App and Volume
```bash
fly apps create my-campaign-vault
fly volumes create campaign_data --region ams --size 1
```

The repo includes a `fly.toml` (currently `app = "my-campaign-vault-for-grok"`). Rename the `app` field to match your Fly app name before deploying.

### 2. Set Security Secrets
```bash
fly secrets set BEARER_TOKEN=your_secure_random_token
```

This enables authentication. See the [Authentication](#authentication) section for all supported ways to pass the token and important security considerations (especially regarding query parameters).

### 3. Deploy
```bash
fly deploy
```

## Authentication

Authentication is **optional** and is only enabled when the `BEARER_TOKEN` environment variable is set.

**Important:** The token is read *exclusively* from the `BEARER_TOKEN` environment variable (never from appsettings.json, user secrets, or command-line arguments). This reduces the risk of accidental secret leakage.

- If `BEARER_TOKEN` is **not set**, the server accepts all requests with no authentication (convenient for local development).
- If `BEARER_TOKEN` **is set**, all requests (except `/` and `/health`) must present a valid token.
- Tokens are compared using a **timing-safe, case-sensitive** match (`CryptographicOperations.FixedTimeEquals`). "MyToken" will **not** match "mytoken". Use a long, random, mixed-case value.

### Supported Authentication Methods

The server checks for a valid token in the following order:

1. **`Authorization` header (recommended)**
   ```http
   Authorization: Bearer your-secure-token-here
   ```

2. **`X-API-Key` header**
   ```http
   X-API-Key: your-secure-token-here
   ```

3. **Query parameter (fallback only)**
   ```
   https://your-app.example.com/?token=your-secure-token-here
   ```
   Also accepts `?auth=...` and `?bearer=...` as aliases.

   **Security warning**: Passing the token in the query string is significantly less secure than using headers. Query parameters are frequently logged by servers, reverse proxies, CDNs, load balancers, and analytics tools. They can also leak through browser history, shared links, or the `Referer` header. Only use this method when your client cannot set custom headers (e.g. Grok Web custom connectors as of version 4.3).

### Recommendations by Environment

| Environment          | Recommended Approach                          | Notes |
|----------------------|-----------------------------------------------|-------|
| Local development    | Do **not** set `BEARER_TOKEN`                 | Simplest and safest for rapid iteration |
| Testing via ngrok    | Use ngrok's `--basic-auth` or a separate low-privilege token | Avoid using your real production token |
| Production (Fly.io, etc.) | Prefer `Authorization: Bearer` header        | Query parameter method should be a last resort |
| Grok Web connectors  | `?token=...` (temporary workaround)           | Switch to headers as soon as Grok supports them |

### Best Practices

- Prefer header-based authentication whenever possible.
- If you must use the query parameter method in production, consider using a dedicated token with limited scope (if you implement additional authorization logic later).
- Rotate tokens periodically, especially if they ever appeared in logs or URLs.
- Treat the token as case-sensitive (exact match). Store it securely (e.g. `fly secrets`, Kubernetes secrets, or a proper secrets manager).
- For stronger protection in cloud environments, consider putting the service behind Cloudflare Access, Tailscale, or a similar identity-aware proxy instead of (or in addition to) the built-in token.

See the Deployment section for how to set `BEARER_TOKEN` on Fly.io.

## Configuration

| Variable | Purpose | Default |
|----------|---------|---------|
| `CAMPAIGN_DB_PATH` | RavenDB data directory | `{AppBase}/RavenData` (Fly.io: `/app/data/campaign.db` via `fly.toml`) |
| `BEARER_TOKEN` | Optional auth token (env only) | unset = no auth |
| `CORS_ALLOWED_ORIGINS` | Comma-separated origins, or `*` | `*` (allow any) |

## Development

See `ARCHITECTURE.md` for the full system design. Key code locations:

- **Models** — `src/CampaignVault/Models/` (`Character`, `WorldChanges`, `SceneView`, ruleset extensions)
- **Repository** — `src/CampaignVault/Data/CampaignRepository.cs` + `JsonSanitizer.cs`
- **Simulation** — `DefaultSimulationEngine` + rules: `ScheduleEvaluationRule`, `NeedsAccumulationRule`, `RumorDecayRule`, `StatusExpiryRule`, `MemorySalienceDecayRule`, `NeedConflictRule`, `FactionEcosystemRule`, `QuestStalenessRule`, `RelationalRearmRule`, `TransientEvictionRule`
- **Pressure** — `src/CampaignVault/Data/Pressure/` (orchestrator + contributors)
- **Rulesets** — `src/CampaignVault/Rulesets/` (D&D 5e, PF2e, Fallout 2d20 resolvers + `DefaultRollService`)
- **Tools** — `src/CampaignVault/Tools/CampaignTools.cs`
- **Tests** — `tests/CampaignVault.Tests/`

## Client Compatibility Notes (as of latest testing)

- `commit` is the most reliable mutation tool across clients.
- The `upsert_*` tools are now strongly typed (`UpsertCharacter(Character)`, `UpsertLocation(Location)`, `UpsertLore(Lore)`). They remain less reliable with Grok Web because the client still sends calls using the original legacy parameter names (`c` and `l`) from an early version of this server (likely due to client-side caching when the connector was first added). For Grok Web users, prefer `commit` (especially `ActivityChange`) for most work.
- `commit` now exposes the full discriminated-union `WorldChange[]` shape directly (with rich per-variant and per-field `[Description]` annotations + `$type` discriminators). This is the clean .NET / STJ polymorphic form Gemini and similar models recommend. A non-exposed `Commit(string json)` fallback remains for clients that still struggle with complex input schemas.
- Use the `activity` change type inside `commit` when narrative implies an NPC should have a new `CurrentActivity` / `CurrentLocationId` (this keeps `get_scene` consistent without requiring `advance_world`).

**Recommended seeding / world-building pattern**:

When introducing a new significant location or NPC, do as much as possible in a single `commit` call rather than multiple tool invocations. Example batch:

- One `event` describing the arrival / introduction
- One or more `activity` changes to place NPCs where the narrative says they are
- Relationship deltas, need adjustments, mood, etc.

See the `commit` tool description and `get_help` for detailed guidance and copy-paste examples.

Full history of robustness improvements lives in the git log and the regression tests in `CampaignRepositoryTests.cs`.
