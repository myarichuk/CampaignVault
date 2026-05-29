# D&D Campaign Vault - Living World DM Engine

A high-bandwidth Model Context Protocol (MCP) server that turns RavenDB into a persistent, reactive simulation engine for long-running D&D (or other TTRPG) campaigns. It is purpose-built for LLM Dungeon Masters who need reliable world state, NPC psychology, rumor lifecycles, and atomic narrative resolution across many sessions.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC tiredness, and escalate unresolved plot threads via the `WorldSimulator`.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`).
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: Every tool response includes `WorldPressure`—proactive alerts about ticking clocks and background events.
- **Unified Fuzzy Search**: Search across lore, characters, and locations in one shot.

## Core Tool Surface (Recommended Workflow)

| Tool              | Purpose                                      | Notes |
|-------------------|----------------------------------------------|-------|
| `get_world_state` | Session kickoff (time + pressure + rumors)   | Call at the start of every session |
| `get_scene`       | Rich scene view (NPCs + items + summaries)   | Primary exploration tool |
| `get_npc_context` | Deep psychological + history view for an NPC | Use before major roleplay |
| `commit`          | **Primary reliable write path**              | Atomic batch of typed changes (including `activity`) |
| `advance_world`   | Time passage + background simulation         | Runs needs, rumor decay, schedule evaluation |
| `search_world`    | Unified fuzzy search                         | Characters, Locations, Lore |
| `recall_history`  | Semantic memory over past events             | — |
| `get_npc_needs`   | Quick view of an NPC's current needs         | — |

**World Builder / Seeding tools** (`upsert_character`, `upsert_location`, `upsert_lore`): These exist but have historically been less reliable with certain MCP clients (Grok Web, Gemini). Prefer seeding and ongoing mutations through `commit` where possible (especially `ActivityChange` for keeping `CurrentActivity`/`CurrentLocationId` in sync with narrative).

## Recommended DM Workflow

Add guidance similar to this to your LLM system prompt:

> You are the Dungeon Master running a persistent living world using **CampaignVault**.
>
> **Core Loop:**
> 1. Start every session with `get_world_state`.
> 2. When the party moves to a new area, use `get_scene`.
> 3. For deep NPC roleplay, call `get_npc_context` (and `get_npc_needs`).
> 4. At the end of scenes, use `commit` with a batch of changes. Use the `activity` change type when narrative implies an NPC has moved or changed what they are doing.
> 5. For travel, long rests, or downtime, call `advance_world` (this runs background simulation).
>
> Prefer `commit` (especially `ActivityChange`) over the `upsert_*` tools for ongoing play, as `commit` is the most reliable mutation path across different MCP clients.

## The Open Psychological Model (Needs, Wants, Fears)

The NPC "Mind" system is intentionally open-ended. There is no closed list of needs.

- Discover needs at runtime via `get_npc_needs`, `get_scene`, and `get_npc_context`.
- Freely invent narrative-appropriate needs (`wanderlust`, `duty`, `guilt`, `debt_pressure`, etc.) and provide human-readable `NeedDescriptors`.
- For initial world building, the `upsert_*` tools exist. In practice, many users find `commit` (with rich `EventOccurred` + `RelationshipChange` + `ActivityChange` + `NeedChange`) to be the more reliable way to evolve the world during play.

Richly seed key NPCs early with deep `Mind` data (Wants/Fears/Knows, custom needs + descriptors, Schedule + Routines, equipment via Items). The simulation and behavioral synthesis will make much better use of that data than shallow characters.

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

- If `BEARER_TOKEN` is **not set**, the server accepts all requests with no authentication (convenient for local development).
- If `BEARER_TOKEN` **is set**, all requests (except `/` and `/health`) must present a valid token.

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
- For stronger protection in cloud environments, consider putting the service behind Cloudflare Access, Tailscale, or a similar identity-aware proxy instead of (or in addition to) the built-in token.

See the Deployment section for how to set `BEARER_TOKEN` on Fly.io.

## Configuration

- `CAMPAIGN_DB_PATH` — Path to the RavenDB data directory (default: `./RavenData` inside the container).
- `BEARER_TOKEN` — When set, enables authentication. Full details (including header vs query parameter behavior and security trade-offs) are in the [Authentication](#authentication) section above.

## Development
- **Models**: `/Models` (Character + NpcMind, WorldChanges including `ActivityChange`, SceneView, etc.).
- **Core Logic**: `/Data/CampaignRepository.cs` + `JsonSanitizer.cs` (central protection against mixed STJ/Newtonsoft `JsonElement` leakage).
- **Simulation**: `DefaultSimulationEngine` + rules in `/Data` (ScheduleEvaluation, NeedsAccumulation, RumorDecay).
- **Tools**: `/Tools/CampaignTools.cs` (MCP surface + tolerant handling for `commit` and `upsert_*` tools).
- **Tests**: `/CampaignVault.Tests` (integration + regression tests for client compatibility fixes).

## Client Compatibility Notes (as of latest testing)

- `commit` is the most reliable mutation tool across clients.
- The `upsert_*` tools were changed to accept JSON as a plain string for better compatibility with Grok Web and similar connectors (they previously failed hard on parameter name binding). For initial creation of major characters/locations you may still need them once, then switch to `commit`.
- `commit` accepts either strongly-typed changes (convenient for direct callers/tests) or `JsonElement[]` (more schema-friendly for strict validators like Gemini).
- Use the `activity` change type inside `commit` when narrative implies an NPC should have a new `CurrentActivity` / `CurrentLocationId` (this keeps `get_scene` consistent without requiring `advance_world`).

**Recommended seeding / world-building pattern** (put this in your LLM instructions):

When introducing a new significant location or NPC, do as much as possible in a single `commit` call rather than multiple tool invocations. Example batch:

- One `event` describing the arrival / introduction
- One or more `activity` changes to place NPCs where the narrative says they are
- Relationship deltas, need adjustments, mood, etc.

See the description of the `changes` parameter on the `commit` tool for a full copy-paste example.

Full history of robustness improvements lives in the git log and the regression tests in `CampaignRepositoryTests.cs`.
