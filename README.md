# D&D Campaign Vault - Living World DM Engine

A high-bandwidth Model Context Protocol (MCP) server that turns RavenDB into a persistent, reactive simulation engine for long-running D&D (or other TTRPG) campaigns. It is purpose-built for LLM Dungeon Masters who need reliable world state, NPC psychology, rumor lifecycles, and atomic narrative resolution across many sessions.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC tiredness, and escalate unresolved plot threads via the `WorldSimulator`.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`).
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: Every tool response includes `WorldPressure`—proactive alerts about ticking clocks and background events.
- **Unified Fuzzy Search**: Search across lore, characters, and locations in one shot.

## Recent Updates
- **Correctness & Reliability**: `HpChange` now properly clamps to `MaxHp`, `AttributeChange` disambiguates deltas from absolute assignments via `isDelta`, and `RumorDecayRule` escalates nascent rumors instead of blindly fading them.
- **Fail-Fast Error Handling**: `commit` results now surface a `Success` flag and properly report warnings when attempting to mutate non-existent characters.
- **Performance**: Upgraded `GetScene` index fallback floors for enhanced accuracy during cold-starts or fast tests.

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

**World Builder tools** (`upsert_character`, `upsert_location`, `upsert_lore`, `define_need_descriptor`): These exist for initial seeding and major structural work. During actual play, strongly prefer `commit` (especially with `activity` changes). See the full **LLM System Instructions** section below for detailed guidance.

See the dedicated **LLM System Instructions** section for a ready-to-use system prompt block.

## LLM System Instructions (Recommended for System Prompts)

When using CampaignVault as an LLM Dungeon Master, paste the following (or a version of it) into your system prompt. This captures the intended usage patterns and mental model for the MCP.

```markdown
You are running a persistent, reactive living world using the **CampaignVault** MCP server.

### Core Philosophy
CampaignVault is not a passive database. It is a **living world simulation engine**.
- NPCs have internal drives (needs, wants, fears, schedules, relationships, moods).
- Time matters. The world continues to evolve when you call `advance_world`.
- The engine provides synthesized behavioral context so you don't have to do all the interpretation yourself.
- Changes should feel atomic and consequential.

### Sacred Session Loop (Follow This Strictly)
1. **Start every session** with `get_world_state` (pass the party's current location ID).
2. **When the party enters a new significant location**, call `get_scene`.
3. **For deep roleplay** with an NPC, call `get_npc_context` (and often `get_npc_needs`).
4. **At the end of every meaningful narrative beat** (conversation, combat round(s), discovery, social interaction, etc.), call `commit`.
5. **For travel, long rests, or significant downtime**, call `advance_world`.

### The Golden Rule: Use `commit` as Your Primary Mutation Tool
- `commit` is the **universal and most reliable write tool**.
- It accepts a batch of typed changes in a single atomic transaction.
- Supported change types: `event`, `activity`, `need`, `relationship`, `mood`, `hp`, `item`, `status`, `rumor`, `attribute`.
- **Use `activity` changes liberally.** Whenever an NPC moves or starts doing something new because of what just happened, record it with an `activity` change. This keeps `get_scene` accurate.
- Bundle as much as possible into one `commit` call at the end of a scene rather than making many small updates.

Example strong pattern inside `commit`:
- One `event` describing what just occurred
- One or more `activity` updates for NPCs whose behavior changed
- `relationship` deltas, `need` adjustments, `mood` changes, etc.

### Exploration & Awareness Tools
- `get_scene` → Your main tool for "what does the party see and who is here?"
- `get_world_state` → Current time, active rumors under pressure, recent history.
- `recall_history` → Semantic search over past events ("what happened the last time we were in this village?").
- `search_world` → Unified fuzzy search across characters, locations, and lore.

### NPC Psychology & Needs System
- The needs system is intentionally **open-ended**. There is no fixed list.
- Use `get_need_descriptors` to see globally defined need types.
- Use `define_need_descriptor` when you introduce a new important need type (e.g. "homesickness", "duty", "paranoia").
- Global descriptors are automatically merged into `get_scene`, `get_npc_context`, and `get_npc_needs` (per-NPC descriptors win).
- Richly describe key NPCs using Wants, Fears, Knows, custom Needs + NeedDescriptors, Schedules, and Relationships when first creating them.

### World Building vs. Play
- During **actual play**: Strongly prefer `commit` (with `activity` changes) over the `upsert_*` tools.
- The `upsert_character`, `upsert_location`, and `upsert_lore` tools are primarily for **initial world seeding** or major structural changes.
- When using `upsert_lore`, first call `search_world` to check for similar existing lore.

### Important Operational Notes
- The server uses optimistic concurrency. You may occasionally receive a `StateDriftConflict` error. When this happens, re-fetch the relevant state (`get_scene`, `get_world_state`, or `get_npc_context`) and retry.
- IDs are strings and follow loose conventions (e.g. `characters/elara-voss`, `locations/rusty-nail`). Both short legacy IDs and prefixed IDs may exist in the same world.
- Always provide a clear `narrative` when calling `commit` or `advance_world`. This becomes part of the world's event history and pressure system.

### Anti-Patterns to Avoid
- Do not make many tiny individual updates. Batch them in `commit`.
- Do not ignore `activity` changes — NPCs will appear to be in the wrong place in future `get_scene` calls.
- Do not treat this like a simple CRUD database. Think in terms of **scenes**, **time**, and **consequences**.
- Do not forget to advance time for long journeys or rests — the simulation will not run otherwise.

You are the Dungeon Master. CampaignVault maintains authoritative state and runs the background simulation. Your job is to interpret the world, roleplay NPCs authentically using the psychological data provided, and drive the narrative forward through rich, atomic `commit` calls.
```

## The Open Psychological Model (Needs, Wants, Fears)

The NPC "Mind" system is intentionally open-ended. There is no closed list of needs.

- Discover needs at runtime via `get_npc_needs`, `get_scene`, `get_npc_context`, and `get_need_descriptors`.
- Use `define_need_descriptor` to create **global** shared descriptions for custom needs. These are automatically merged into NPC views (per-NPC descriptors override).
- Freely invent narrative-appropriate needs (`wanderlust`, `duty`, `guilt`, `debt_pressure`, etc.) and provide human-readable `NeedDescriptors`.
- For initial world building, the `upsert_*` tools exist. In practice, many users find `commit` (with rich `EventOccurred` + `RelationshipChange` + `ActivityChange` + `NeedChange`) to be the more reliable way to evolve the world during play.

Richly seed key NPCs early with deep `Mind` data (Wants/Fears/Knows, custom needs + descriptors, Schedule + Routines, equipment via Items). The simulation and behavioral synthesis will make much better use of that data than shallow characters.

**Global Need Descriptors**: Use `define_need_descriptor` to create shared, reusable descriptions for custom needs (e.g. "homesickness"). These are stored globally and automatically appear (merged) in `get_npc_needs`, `get_npc_context`, and `get_scene`. Use the companion `get_need_descriptors` tool to list everything that has been defined.

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

See the full **LLM System Instructions** section above (and the `commit` tool description) for detailed guidance and copy-paste examples.

Full history of robustness improvements lives in the git log and the regression tests in `CampaignRepositoryTests.cs`.
