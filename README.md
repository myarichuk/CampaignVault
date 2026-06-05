# D&D Campaign Vault - Living World DM Engine

A high-bandwidth Model Context Protocol (MCP) server that turns RavenDB into a persistent, reactive simulation engine for long-running D&D (or other TTRPG) campaigns. It is purpose-built for LLM Dungeon Masters who need reliable world state, NPC psychology, rumor lifecycles, and atomic narrative resolution across many sessions.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC tiredness, and escalate unresolved plot threads via the `WorldSimulator`.
- **Multi-System Ruleset Engine**: Full polymorphic support for **D&D 5e**, **Pathfinder 2e**, and **Fallout 2d20**. The C# MCP handles math, advantage, 4-degrees of success, and dice pools deterministically.
- **Structured Combat Encounters**: Start, advance, and resolve tactical combat with turn-order tracking, dynamic initiative, and real-time HP/status mutations.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`). The LLM instantly receives the `ActiveCombat` state and `SystemStats` (AC, SPECIAL, etc.) for everyone present.
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: Every tool response includes `WorldPressure`—proactive alerts about ticking clocks and background events.
- **Unified Fuzzy Search**: Search across lore, characters, and locations in one shot.

## Recent Updates
- **Multi-Campaign Support**: Fully isolated campaign contexts with `select_campaign`, `create_campaign`, and `set_active_system` (with system lock-in).
- **Ruleset Integration & Combat**: `RulesetAction` mutations, a polymorphic `SystemExtension` for stats, deterministic resolvers (D&D 5e, PF2e, Fallout 2d20), and dedicated combat turn tracking (`start_combat`, `next_turn`, `end_combat`) natively wired into `get_scene`.
- **Correctness & Reliability**: `HpChange` clamps to `MaxHp`, `AttributeChange` uses `isDelta`, and status modifiers/expiry are active.

## Core Tool Surface

| Tool                   | Purpose                                           | Primary Usage |
|------------------------|---------------------------------------------------|---------------|
| `get_world_state`      | Session kickoff (time + pressure + rumors)        | Start of every session |
| `get_scene`            | Rich scene view (NPCs + behavioral summaries + items + rumors) | When entering a location |
| `get_npc_context`      | Deep psychological profile + recent history       | Before major NPC roleplay |
| `commit`               | **Universal atomic write tool**                   | End of every narrative beat (combat, conversation, discovery, etc.) |
| `advance_world`        | Time passage + full background simulation         | Travel, long rests, downtime |
| `search_world`         | Unified fuzzy search across everything            | Discovery / avoiding duplicates |
| `recall_history`       | Semantic search over past events                  | "What happened last time...?" |
| `get_npc_needs`        | Current needs + merged descriptors for an NPC     | Quick psychological read |
| `get_need_descriptors` | List globally defined need descriptions           | Before introducing new need types |

**World Builder tools** (`upsert_character`, `upsert_location`, `upsert_lore`, `define_need_descriptor`): These exist for initial seeding and major structural work. During actual play, strongly prefer `commit` (especially with `activity` changes). See the recommended system prompt in `docs/recommended-system-prompt.md` for detailed guidance.

**Open-World Flavor, Transients & Laziness Mitigation (Phase 6+)**: The system is deliberately designed so an LLM DM can be "lazy" or exploratory without breaking the world model. Most narration (crowds, one-off details, unnamed NPCs) stays ephemeral. Only meaningful things are persisted via small `commit` payloads using `location_create` / `character_create` / `item_create` etc. 

- `get_scene` returns `PointsOfInterest` (light list) and uses `AmbientCrowd` hints for flavor without creating documents.
- The engine auto-links maps on `location_create` (supply `connectedFromLocationId`).
- Transients (created without `schedule` + `keepAlive:false`) are auto-evicted by `TransientEvictionRule` during `advance_world` when areas go "cold".
- **Critical**: `get_scene`, `get_world_state`, and `advance_world` return `WorldPressure` containing `ENGINE WARNING:` and `NARRATIVE PROMPT:` items. These include **exact copy-paste JSON** for the `commit` needed to fix hallucinations, dead-ends, empty-but-expected-crowds, broken links, etc. Treat them as mandatory directives. Call `get_help` for the full "Lazy Tavern" walkthrough and patterns.
- This directly addresses the "silly factor" of forcing perfect polymorphic JSON arrays for every flavor element the LLM narrates.

See `get_help`, the recommended system prompt, and `docs/Phase6_OpenWorld_Design.md` for details. The `phase7.md` tracks work on travel/spatial, factions, and quests.

## Deep Mechanics (Phase 7: Factions, Quests, Travel)

The engine provides deep structural tracking for macro-mechanics:
- **Factions**: Track influence, wealth, and stance matrices. Use `FactionCreate`, `FactionReputationChange`, and `FactionPresenceChange` to mutate them. Background rules shift their influence over time, and the engine pressures the LLM to reflect these shifts in local scenes. Use `get_faction_context` to do a deep dive on a specific faction.
- **Quests**: Manage long-term objectives with strict state tracking (Open, InProgress, Complete, Failed). Quests decay towards deadlines as time passes, emitting `Quest:Stale` and `Quest:ApproachingDeadline` pressures so the DM doesn't forget them. Use `get_quest_details` to pull the full objective list and history.
- **Travel**: When a character starts traveling, they use an `ActivityChange` but do NOT update their `LocationId`. If they don't arrive within the expected timeframe, the engine raises an `ENGINE WARNING: Travel Interrupted` pressure to prevent characters from getting permanently stuck in limbo, forcing the LLM to conclude the encounter and `LocationChange` them to their destination.



## The Open Psychological Model (Needs, Wants, Fears)

The NPC "Mind" system is intentionally open-ended. There is no closed list of needs.

- Discover needs at runtime via `get_npc_needs`, `get_scene`, `get_npc_context`, and `get_need_descriptors`.
- Use `define_need_descriptor` to create **global** shared descriptions for custom needs. These are automatically merged into NPC views (per-NPC descriptors override).
- Freely invent narrative-appropriate needs (`wanderlust`, `duty`, `guilt`, `debt_pressure`, etc.) and provide human-readable `NeedDescriptors`.
- For initial world building, the `upsert_*` tools exist. In practice, many users find `commit` (with rich `EventOccurred` + `RelationshipChange` + `ActivityChange` + `NeedChange`) to be the more reliable way to evolve the world during play.

Richly seed key NPCs early with deep `Mind` data (Wants/Fears/Knows, custom needs + descriptors, Schedule + Routines, equipment via Items). The simulation and behavioral synthesis will make much better use of that data than shallow characters.

**Global Need Descriptors**: Use `define_need_descriptor` to create shared, reusable descriptions for custom needs (e.g. "homesickness"). These are stored globally and automatically appear (merged) in `get_npc_needs`, `get_npc_context`, and `get_scene`. Use the companion `get_need_descriptors` tool to list everything that has been defined.



## Deployment to Fly.io

### 1. Create the App and Volume
```bash
fly apps create my-campaign-vault-v4
fly volumes create campaign_data --region ams --size 1
```

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

- `CAMPAIGN_DB_PATH` — Path to the RavenDB data directory (default: `./RavenData` inside the container).
- `BEARER_TOKEN` — When set, enables authentication. Full details (including header vs query parameter behavior and security trade-offs) are in the [Authentication](#authentication) section above.

## Development
- **Models**: `/Models` (Character + NpcMind, WorldChanges including `ActivityChange`, SceneView, etc.).
- **Core Logic**: `/Data/CampaignRepository.cs` + `JsonSanitizer.cs` (central protection against mixed STJ/Newtonsoft `JsonElement` leakage).
- **Simulation**: `DefaultSimulationEngine` + rules in `/Data` (ScheduleEvaluation, NeedsAccumulation, RumorDecay).
- **Tools**: `/Tools/CampaignTools.cs` (MCP surface; `upsert_*` tools are strongly typed, with notes on current Grok Web client behavior).
- **Tests**: `/CampaignVault.Tests` (integration + regression tests for client compatibility fixes).

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

See the `commit` tool description and `docs/recommended-system-prompt.md` for detailed guidance and copy-paste examples.

Full history of robustness improvements lives in the git log and the regression tests in `CampaignRepositoryTests.cs`.
