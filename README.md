# CampaignVault - Living World DM Engine

Turn an LLM into a DM that remembers your world. Track NPCs, time, combat, and consequences across sessions without hallucinations or context limits.

**Ruleset support:** D&D 5e, Pathfinder 2e, Narrative (d6 Oracle).

---

## Use Case 1: Local Development (Your Computer)

Run CampaignVault on your machine and test locally. Great for building campaigns.

### Quick Start

1. **Install Docker** (if not already installed)

2. **Build the image:**
   ```bash
   docker build -t campaignvault:latest -f Dockerfile .
   ```

3. **Run the server:**
   ```bash
   docker run -p 5275:5275 -e CAMPAIGN_DB_PATH=/app/data -v campaign_data:/app/data campaignvault:latest
   ```

4. **Test it's working:**
   ```bash
   curl http://localhost:5275/health
   ```

5. **Connect your LLM (Claude, Grok, etc.):**
   - Point it to `http://localhost:5275` (no authentication needed locally)
   - The LLM will auto-discover all available tools

That's it. Start narrating and use the tools as your LLM encounters them.

---

## Use Case 2: Remote Deployment (Play Anywhere)

Deploy to the cloud and access from any device. Share campaigns with collaborators.

### Option A: ngrok (Easiest, Temporary)

Use ngrok to expose your local server to the internet (good for testing before permanent deployment).

1. **Download ngrok** from https://ngrok.com

2. **Start your local server** (see Use Case 1, step 3)

3. **Expose it with ngrok:**
   ```bash
   ngrok http 5275
   ```
   ngrok will print a public URL like `https://abc123.ngrok.io`

4. **In your LLM connector**, use that URL

5. **For security**, set a token:
   ```bash
   docker run -p 5275:5275 \
     -e BEARER_TOKEN=your-secure-random-token \
     -e CAMPAIGN_DB_PATH=/app/data \
     -v campaign_data:/app/data \
     campaignvault:latest
   ```
   Then pass the token in your LLM connector headers: `Authorization: Bearer your-secure-random-token`

### Option B: Fly.io (Permanent Deployment)

Deploy to Fly.io for a real production setup with persistent storage.

1. **Install Fly CLI:**
   ```bash
   # macOS
   brew install flyctl
   
   # Linux / Windows: https://fly.io/docs/hands-on/install-flyctl/
   ```

2. **Create an app and storage:**
   ```bash
   fly apps create my-campaign-vault
   fly volumes create campaign_data --region ams --size 1
   ```
   (Change region and app name to your preference.)

3. **Set a secure token:**
   ```bash
   fly secrets set BEARER_TOKEN=your-secure-random-token
   ```

4. **Deploy:**
   ```bash
   fly deploy
   ```

5. **Your app is live** at `https://my-campaign-vault.fly.dev`

6. **In your LLM connector**, use that URL with the token in headers (see ngrok step 5).

---

## What It Does

### World State That Persists

- **Campaign memory:** NPCs, locations, lore, factions, quests all survive session to session
- **Time tracks forward:** Days pass, resources recover, rumors decay, factions get impatient
- **Combat state:** Initiative, turn order, HP, status effects—all tracked and queryable
- **NPC psychology:** Wants, fears, relationships, mood—the LLM gets behavioral hints to roleplay authentically

### Prevents Hallucination

- All **scenes, NPCs, and events** are pulled from the database, not made up
- LLM can't accidentally contradict prior decisions
- You get **proactive warnings** ("This NPC should be tired", "This quest deadline is approaching")

### Atomic Mutations

- Change one NPC's health, another's mood, and move a third—all in one commit
- No transaction conflicts or partial failures
- Perfect for resolving combat or complex narrative moments

### Open World Play

- Add flavor on the fly (crowds, one-off details) without cluttering the database
- System auto-cleans transient content when areas go "cold"
- No penalty for being "lazy" or exploratory

---

## Core Tools (Sample)

| What you want | Tool |
|---|---|
| Start a session | `get_world_state` — time, active quests, NPCs in crisis |
| Enter a location | `get_scene` — people, items, rumors, combat state |
| Understand an NPC | `get_npc_context` — psychology, memories, current mood |
| Resolve an action | `commit` — HP changes, item transfers, time passing, events |
| Find something | `search_world` — keyword search across everything |
| Progress time | `advance_world` — days/weeks pass, simulation rules fire |

For full tool list, run `get_help` inside your campaign (built-in DM manual with examples).

---

## Setup: Authentication & Configuration

### Local (no auth):
```bash
docker run -p 5275:5275 campaignvault:latest
```

### Remote (with token):
```bash
fly secrets set BEARER_TOKEN=your-token
# or
docker run -p 5275:5275 -e BEARER_TOKEN=your-token campaignvault:latest
```

Pass the token in your LLM connector:
- **Header (recommended):** `Authorization: Bearer your-token`
- **Header (alternate):** `X-API-Key: your-token`
- **Query parameter (fallback):** `?token=your-token` (less secure—logs may capture it)

### All Configuration Options

| Variable | Purpose | Default |
|---|---|---|
| `CAMPAIGN_DB_PATH` | Where campaigns live on disk | `{AppBase}/RavenData` |
| `BEARER_TOKEN` | Auth token (optional) | unset = no auth |
| `CORS_ALLOWED_ORIGINS` | Allowed client origins | `*` (any) |
| `MCP_PORT` | HTTP server port | `5275` (or `8080` in Docker) |

---

## Ruleset-Specific Notes

### D&D 5e
- Uses SRD 5.1 rules (ability checks, saving throws, advantage, etc.)
- Spell slots auto-recover on rest
- Hit die for character creation

### Pathfinder 2e
- Full ORC License support
- 4-degrees-of-success resolution
- Conditions and persistent damage

### Narrative
- d6 Oracle resolution (no classes/levels required)
- Pure story-focused mechanics
- Great for experimental or indie rulesets

---

## Next Steps

1. **Start locally** to get familiar with the campaign/session flow
2. **Try an example flow:** create campaign → load scene → resolve combat → advance time
3. **Read `ARCHITECTURE.md`** if you want to understand the innards (optional, not needed for play)
4. **Check `get_help`** for copy-paste patterns and advanced workflows

---

## Licensing

**Code:** Dual-licensed
- **Personal/non-commercial use:** Free (PolyForm Noncommercial 1.0.0)
- **Commercial use:** Requires a commercial license. See [COMMERCIAL.md](./COMMERCIAL.md)

**Game Content:**
- D&D 5e uses official SRD (CC-BY-4.0)
- Pathfinder 2e uses ORC License
- See [LICENSING.md](./LICENSING.md) for full details

**Note:** RavenDB Community Edition (used in local/dev deployments) requires a free license key for production. See [COMMERCIAL.md](./COMMERCIAL.md).

See [LICENSING.md](./LICENSING.md) for complete game-content attribution and legal notes.

**Important**: Production deployments must obtain a free RavenDB Community license key (https://ravendb.net/license/request/community) to deploy legally. See [COMMERCIAL.md](./COMMERCIAL.md) for details.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC tiredness, and surface aging unresolved events as pressure via `DefaultSimulationEngine` and its simulation rules.
- **Multi-System Ruleset Engine**: Full polymorphic support for **D&D 5e**, **Pathfinder 2e**, and a brand new **Narrative** ruleset featuring a d6 Oracle. The C# MCP handles math, advantage, 4-degrees of success, and dice pools deterministically.
- **Structured Combat Encounters**: Start, advance, and resolve tactical combat with ruleset initiative rolls at `start_combat`, turn-order tracking, and HP/status mutations applied atomically via `commit`.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`). The LLM instantly receives the `ActiveCombat` state and `SystemStats` (AC, SPECIAL, Temperature, WarmthRating, MovementModifier, etc.) for everyone present.
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: `get_world_state`, `get_scene`, and `advance_world` surface `WorldPressure`—proactive alerts about ticking clocks and background events. `get_npc_context` can also surface urgent initiative pressures.
- **Unified Search**: Keyword/wildcard search across lore, characters, and locations in one shot (`search_world`).

## Recent Updates
- **Equipment-Derived Movement Modifier**: Characters now track `MovementModifier` computed from equipped items' `speedModifier` properties (negative = penalty, positive = bonus). Same pattern as `WarmthRating`: narrative-only, recomputed on every `item_equip`/`item_unequip`, not enforced by travel. The LLM can assign `speedModifier` to any item based on narrative context (heavy armor, uncomfortable sandals, enchanted boots, etc.) — not hardcoded restraints.
- **Outfit Batch UX**: Multi-item outfit swaps work by committing multiple `item_equip`/`item_unequip` changes in a single atomic `commit` call (one JSON array). No separate outfit tool needed. `equipZones`/`equipLayer` are set once via `world_build` (or `item_update`), not on `item_equip`.
- **Climate & Weather**: Locations carry a `climateZone` (Arctic, Tundra, Temperate, Desert, Tropical, Alpine, Subterranean, inherited from parent if unset); ambient temperature varies by zone and time of day. Characters' felt temperature = ambient + equipped-item `WarmthRating` — insulation helps in the cold and hurts in the heat (furs are protective in the Arctic, dangerous in the Desert). Sustained extremes surface as narrative pressure; there's no automatic mechanical penalty, the consequence call stays with the DM-LLM.
- **World Seeding via `world_build`**: One atomic batch call seeds an entire campaign's opening state — locations, factions, characters, items, quests, plot threads, lore, rumors, and homebrew creatures/spells/feats — in a fixed dependency order with all-or-nothing rollback on a bad entry. Replaces the older one-tool-per-entity-kind upsert surface. See `get_help topic=world-building`.
- **Engagement Relations & Spatial Positioning**: Pairwise scene anchors (`engagement_relation`: category + freeform verb) vs. relative placement (`spatial_position`: distance band, bearing, zone). Category defaults control travel blocks and scene pressure; ruleset resolvers auto-establish/clear grapple engagements on contested maneuver checks. See `get_help` and `ARCHITECTURE.md`.
- **Multi-Campaign Support**: Per-campaign singletons (time, combat, config) with `create_campaign`, `list_campaigns`, and `set_active_system` (with system lock-in). Every campaign-scoped tool requires an explicit **`campaignName`** slug — the MCP HTTP transport is stateless (no session selection). Shared-universe canon (no `CampaignName`) appears in every campaign; campaign-owned entities are slug-tagged.
- **Ruleset Integration & Combat**: `RulesetAction` mutations, a polymorphic `SystemExtension` for stats, deterministic resolvers (D&D 5e, PF2e, Narrative), and dedicated combat turn tracking (`start_combat`, `next_turn`, `end_combat`) natively wired into `get_scene`.
- **Correctness & Reliability**: `HpChange` clamps to `MaxHp`, `AttributeChange` uses `isDelta`, and status modifiers/expiry are active.
- **Character Bootstrap Pipeline**: Per-ruleset HP/defense/proficiency derivation when PCs omit `maxHp` on create/upsert; `level_up` for incremental gains. Put `hitDie`/`level` on typed `systemStats` (not `attributes`). Creature stat blocks use `statBlockHp` or `maxHp` (HP formula only — AC/proficiency still derive).

## Core Tool Surface

### Session & exploration

| Tool | Purpose |
|------|---------|
| `get_current_campaign` | Campaign context for a slug: ruleset, lock-in, party posture |
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
| `get_config` / `set_active_system` | Read or set active ruleset (D&D 5e, PF2e, Narrative) |
| `start_combat` / `next_turn` / `end_combat` | Initiative at start, turn tracking, round-based status expiry |

### Campaign management

| Tool | Purpose |
|------|---------|
| `create_campaign` / `list_campaigns` | Create and list campaigns (pass slug as `campaignName` on all other tools) |

### Deep dives

| Tool | Purpose |
|------|---------|
| `get_faction_context` | Full faction document (stances, territory, `EconomicDemand`) |
| `get_quest_details` | Full quest document (objectives, deadlines, progress timestamps) |

### World builder

| Tool | Purpose |
|------|---------|
| `world_build` | Atomic batch create/update for any entity kind (locations, factions, characters, items, quests, plotThreads, lore, rumors, creatures, spells, feats) — the primary tool for initial seeding and major structural work |
| `define_need_descriptor` / `get_need_descriptors` | Per-campaign shared descriptions for custom NPC needs |

During actual play, strongly prefer `commit` (especially with `activity` changes) over re-calling `world_build`. Call `get_help topic=world-building` for the seeding-order guide and a copy-paste example.

**Open-World Flavor, Transients & Laziness Mitigation**: The system is deliberately designed so an LLM performing the DM role can be "lazy" or exploratory without breaking the world model. Most narration (crowds, one-off details, unnamed NPCs) stays ephemeral. Only meaningful things are persisted — new entities via `world_build`, incremental changes via small `commit` payloads.

- `get_scene` returns `PointsOfInterest` (light list) and uses `AmbientCrowd` hints for flavor without creating documents.
- The engine auto-links maps when a new location's `world_build` entry sets `connectedFromLocationId`.
- Transients (created without `schedule` + `keepAlive:false`) are auto-evicted by `TransientEvictionRule` during `advance_world` when areas go "cold".
- **Critical**: `get_scene`, `get_world_state`, and `advance_world` return `WorldPressure` containing `ENGINE WARNING:` and `NARRATIVE PROMPT:` items. These include **exact copy-paste JSON** for the `commit` needed to fix hallucinations, dead-ends, empty-but-expected-crowds, broken links, etc. Treat them as mandatory directives. Call `get_help` for the full "Lazy Tavern" walkthrough and patterns.
- This directly addresses the "silly factor" of forcing perfect polymorphic JSON arrays for every flavor element the LLM narrates.

See `get_help` for the full DM manual (engagements, spatial positions, grapple patterns). See `ARCHITECTURE.md` for scoping, simulation, engagement/spatial design, and ruleset integration. See [recommended-system-prompt.md](./recommended-system-prompt.md) for a copy-paste LLM system prompt (or [recommended-system-prompt.opencode.md](./recommended-system-prompt.opencode.md) plus [opencode Integration](#opencode-integration) below if you're using opencode).

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

- **Invent any need.** The system is completely unrestricted: `paranoia`, `wanderlust`, `obsession`, `debt_pressure`, `homesickness`, `vengeance`, whatever fits the narrative. Custom needs automatically get evocative activity-conflict framings.
- Discover needs at runtime via `get_npc_needs`, `get_scene`, `get_npc_context`, and `get_need_descriptors`.
- Use `define_need_descriptor` to create **per-campaign** shared descriptions for custom needs. These are automatically merged into NPC views (per-NPC descriptors override).
- For initial world building, `world_build` exists. In practice, many users find `commit` (with rich `event` + `relationship` + `activity` + `need` changes — the `$type` discriminators `commit` actually expects, backed by the `EventOccurred`/`RelationshipChange`/`ActivityChange`/`NeedChange` C# types) to be the more reliable way to evolve the world during play.

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
| `MCP_PORT` | HTTP MCP + health listener port | `5275` (Fly: `8080`) |
| `MCP_BIND_ANY` | Bind `0.0.0.0` instead of `localhost` | `1` in Docker/Fly; `0` in local Development |
| `MCP_STDIO` | Enable stdio MCP transport | unset |
| `GRPC_PORT` | gRPC sync port for authoring UI | `50051` |

### Campaign scoping

Pass **`campaignName`** (campaign slug) on every campaign-scoped tool call. There is no per-session or process-wide campaign selection.

## Development

Code is in `src/CampaignVault/`. Key folders:
- **Models:** Character, Scene, NPC Mind, ruleset extensions
- **Tools:** LLM-facing APIs (`get_scene`, `commit`, etc.)
- **Rulesets:** D&D 5e, Pathfinder 2e, Narrative resolvers
- **Simulation:** Background rules (time, NPC mood, quest decay, etc.)

- **Models** — `src/CampaignVault/Models/` (`Character`, `WorldChanges`, `SceneView`, ruleset extensions)
- **Repository** — `src/CampaignVault/Data/CampaignRepository.cs` + `JsonSanitizer.cs`
- **Simulation** — `DefaultSimulationEngine` + rules: `ScheduleEvaluationRule`, `NeedsAccumulationRule`, `RumorDecayRule`, `StatusExpiryRule`, `MemorySalienceDecayRule`, `NeedConflictRule`, `ClimateExposureRule`, `FactionEcosystemRule`, `QuestStalenessRule`, `RelationalRearmRule`, `AmbientItemDecayRule`, `TransientEvictionRule`, `ResourceRecoveryRule`
- **Pressure** — `src/CampaignVault/Data/Pressure/` (orchestrator + contributors)
- **Rulesets** — `src/CampaignVault/Rulesets/` (D&D 5e, PF2e, Fallout 2d20, Narrative resolvers + `DefaultRollService`)
- **MCP tools** — `src/CampaignVault/Tools/*Tools.cs` (domain classes; `CampaignTools.cs` is a test-only facade)
- **Authoring UI** — connects via gRPC on `GRPC_PORT`; for play/testing against a local MCP server, pass `campaignName` on each tool call
- **Tests** — `tests/CampaignVault.Tests/`

Test suite: `tests/CampaignVault.Tests/` and `tests/CampaignVault.IntegrationTests/`

## Client Compatibility Notes (as of latest testing)

- `commit` is the most reliable mutation tool across clients.
- The individual `upsert_character`/`upsert_location`/etc. tools were retired in favor of a single `world_build` batch tool (struct-of-typed-arrays: `characters[]`, `locations[]`, etc.) — one call seeds everything atomically instead of one round-trip per entity. See `get_help topic=world-building`.
- `commit` exposes the full discriminated-union `WorldChange[]` shape directly (with rich per-variant and per-field `[Description]` annotations + `$type` discriminators). This is the clean .NET / STJ polymorphic form Gemini and similar models recommend. A non-exposed `Commit(string json)` fallback remains for clients that still struggle with complex input schemas.
- Use the `activity` change type inside `commit` when narrative implies an NPC should have a new `CurrentActivity` / `CurrentLocationId` (this keeps `get_scene` consistent without requiring `advance_world`).

**Recommended seeding / world-building pattern**:

Use `world_build` for initial seeding (session 0) — one atomic batch call rather than one tool invocation per entity. For incremental changes during play, do as much as possible in a single `commit` call. Example `commit` batch when introducing a new NPC into an existing scene:

- One `event` describing the arrival / introduction
- One or more `activity` changes to place NPCs where the narrative says they are
- Relationship deltas, need adjustments, mood, etc.

See the `commit` tool description and `get_help` for detailed guidance and copy-paste examples.

Full history of robustness improvements lives in the git log and the regression tests in `CampaignRepositoryTests.cs`.

## opencode Integration

If you're playing through [opencode](https://opencode.ai), CampaignVault ships a dedicated plugin plus a one-shot setup script instead of the generic copy-paste system prompt:

- **[opencode-plugin/](./opencode-plugin/)** — a TypeScript opencode plugin that mechanically enforces several of the rules the generic system prompt otherwise has to spell out in prose:
  - Surfaces `ENGINE WARNING` pressure and the 5-warning escalation cap as a toast (`tool.execute.after`), and prepends a pre-rendered STATUS BAR block to `start_session`/`take_turn`/`get_entity` output so the model doesn't have to reconstruct it from memory.
  - Re-injects the `CAMPAIGN` line and a state-refresh nudge after a compaction/idle gap (`session.compacted`/`session.idle`), so persisted state stays the source of truth across summarization.
  - Hard-blocks bash/script commands that look like an attempt to fake a dice roll (`tool.execute.before`) — CampaignVault resolves all rolls server-side via `take_turn`.
  - Build/test it standalone with `cd opencode-plugin && npm install && npm run build && npm test`.
- **[recommended-system-prompt.opencode.md](./recommended-system-prompt.opencode.md)** — the opencode-specific system prompt. Same structure as `recommended-system-prompt.md`, but the pressure/state-persistence/dice rules and the STATUS BAR section are shortened since the plugin above enforces them mechanically. Use this one (not the generic file) when the plugin is installed.
- **[scripts/setup-opencode.sh](./scripts/setup-opencode.sh)** / **[scripts/setup-opencode.ps1](./scripts/setup-opencode.ps1)** — wires up a campaign directory for opencode in one step: writes `AGENTS.md` from `recommended-system-prompt.opencode.md` (falling back to the generic prompt with a warning if that file is missing), copies the `dnd-*` skills into `.opencode/skills/`, builds the plugin and copies it into `.opencode/plugin/`, and writes/merges `opencode.json` with the MCP server entry and the plugin registration.

  ```bash
  ./scripts/setup-opencode.sh /path/to/campaign --slug my-campaign --ruleset Dnd5e \
    --roster "chars/valen - Valen, chars/nia - Nia" --mcp-port 5275
  ```

  This assumes the CampaignVault MCP server is already running locally (`dotnet run`, default port 5275) — start that separately before launching opencode against the campaign directory.

## Troubleshooting

**"Docker daemon is not running"** → Start Docker and try again.

**"campaignvault:latest image not found"** → Run `docker build -t campaignvault:latest -f Dockerfile .` first.

**"Connection refused"** → Make sure the port (5275 local, 8080 in Docker) is exposed and the container is running.

**"Unauthorized" (remote)** → Check your `BEARER_TOKEN` is set and passed in headers or query params.

---

For deeper docs: [ARCHITECTURE.md](./ARCHITECTURE.md) | [System Prompt](./recommended-system-prompt.md) | [opencode Integration](#opencode-integration)
