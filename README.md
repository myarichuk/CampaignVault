# D&D Campaign Vault - Living World DM Engine (V4)

A high-bandwidth Model Context Protocol (MCP) server that transforms RavenDB into an authoritative, persistent simulation engine for D&D campaigns. Designed specifically for LLM Dungeon Masters, it minimizes tool-chatter while maximizing world fidelity.

## Features
- **Living World Simulation**: Background processes naturally decay rumors, accumulate NPC fatigue, and escalate unresolved plot threads via the `WorldSimulator`.
- **Scene-Centric Workflow**: Load entire locations, NPCs, rumors, and visible items in a single call (`get_scene`).
- **Psychological NPC Minds**: NPCs have Wants, Fears, Moods, and Relationships. The engine synthesizes behavioral summaries to help the LLM roleplay them authentically.
- **Atomic Scene Resolution**: Commit an entire combat's worth of HP deltas, item transfers, and status changes in one transaction (`commit`).
- **Situational Awareness**: Every tool response includes `WorldPressure`—proactive alerts about ticking clocks and background events.
- **Unified Fuzzy Search**: Search across lore, characters, and locations in one shot.

## The V4 Tool Surface

| Tool | Purpose | Key Parameters |
| :--- | :--- | :--- |
| `get_world_state` | **KICKOFF**: Loads time, high-pressure rumors, and party location. | `partyLocationId` |
| `get_scene` | **EXPLORATION**: Loads full NPC/Item data for a specific location. | `locationId` |
| `commit` | **RESOLUTION**: Atomically applies a batch of typed world changes. | `changes[]`, `narrative` |
| `advance_world` | **TIME PASSAGE**: Skips days, runs simulation, and logs rest/travel. | `days`, `timeOfDay` |
| `get_npc_context` | **ROLEPLAY**: Psychological deep dive (Mind, Relationships, History). | `characterId` |
| `search_world` | **GLOBAL SEARCH**: Unified fuzzy search across all world entities. | `query` |
| `recall_history` | **MEMORY**: Semantic retrieval of past campaign events. | `query`, `limit` |

## DM Operational Protocol (V4)

Add this to your LLM system instructions:

> You are the Dungeon Master. You operate using the **CampaignVault V4 Engine**.
> 
> **Workflow Protocol:**
> 1. **Session Start**: ALWAYS call `get_world_state` first to orient yourself to the time, active rumors, and world pressure.
> 2. **Exploration**: When the party enters a new room or region, call `get_scene`. This provides you with ALL present NPCs and items; you do not need to search for them individually.
> 3. **Roleplay**: Before an intense NPC interaction, call `get_npc_context` to understand their mood, goals, and history with the party.
> 4. **Scene Resolution**: At the end of a combat or conversation, call `commit` with a batch of changes (HP adjustments, items moved, rumors evolved). Do not update characters piece-meal.
> 5. **Downtime**: Use `advance_world` for travel or rests. This triggers background simulations that you must incorporate into your narration.
> 6. **World Pressure**: Pay attention to the `WorldPressure` field in tool responses—it tells you which plot threads are aging or which rumors are about to peak.

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

### 3. Deploy
```bash
fly deploy
```

## Configuration
- `CAMPAIGN_DB_PATH`: Path to RavenDB data (Default: `./RavenData`).
- `BEARER_TOKEN`: Secret key for API authentication.

## Development
- **Models**: `/Models` (NpcMind, WorldChanges, SceneView).
- **Engine**: `/Data/WorldSimulator.cs` (Time-passage logic).
- **Repository**: `/Data/CampaignRepository.cs` (RavenDB transactional logic).
- **Tests**: `/CampaignVault.Tests` (Full V4 integration suite).
