# D&D Campaign Vault - MCP Server (RavenDB)

A robust Model Context Protocol (MCP) server for managing D&D 5e campaign state. Powered by RavenDB Embedded, this vault provides Grok (or any DM LLM) with authoritative persistence for characters, lore, and session events.

## Features
- **Persistent State**: Powered by RavenDB (NoSQL document database).
- **Fuzzy Search**: Lucene-based search for lore and history that handles typos and partial matches.
- **Drift Hardening**: Optimistic concurrency prevents the LLM from overwriting data based on stale context.
- **Enhanced NPC Models**: Track "Needs" (numerical key-values) and "Knowledge Graphs" (relationships).
- **LLM-Optimized**: Rich descriptions and structured error handling for seamless AI interaction.

## Detailed Tool List

| Tool | Purpose | Key Parameters |
| :--- | :--- | :--- |
| `get_character` | Retrieve authoritative stats and status. | `identifier` (ID or Name) |
| `upsert_character` | Create or fully replace a character sheet. | `character` (Object) |
| `update_character` | Partial updates (HP, Status, Needs, Notes). | `identifier`, `updates` (Dict) |
| `query_lore` | Search world info using **fuzzy matching**. | `query`, `tags`, `category` |
| `upsert_lore` | Create/update NPC bios, locations, facts. | `lore` (Object) |
| `log_event` | Record significant campaign beats. | `summary`, `type`, `involved` |
| `query_events` | Recall session history and past deeds. | `query`, `type`, `limit` |

## Grok-Side Setup

### DM System Prompt
Add this to your DM instructions to ensure Grok uses the tools effectively:

> You are the Dungeon Master. You have access to the **CampaignVault** tools to maintain a persistent, authoritative world state. 
> 
> **Operational Protocol:**
> 1. **Stat Verification**: ALWAYS call `get_character` before narrating NPC or PC actions to ensure HP, status, and needs are correct.
> 2. **Real-time Updates**: 
>    - Call `update_character` immediately after HP changes or status effects are applied.
>    - Call `upsert_lore` when you invent new NPCs, locations, or historical facts.
>    - Call `log_event` at the end of every major scene to record the "history" of the world.
> 3. **Memory Retrieval**: At the start of every session, call `query_events` and `get_character` for all players to catch up.
> 4. **Handling Conflicts**: If a tool returns a `StateDriftConflict` error, your context is stale. Call the corresponding `get` or `query` tool to refresh your memory, then retry your update.
> 
> Your goal is to keep the Vault so accurate that any other instance of yourself could take over the campaign seamlessly.

## Deployment to Fly.io

### 1. Create the App and Volume
```bash
# Create the app
fly apps create my-campaign-vault-for-grok

# Create a 1GB persistent volume for RavenDB
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
- **Models**: `/Models` (POCOs).
- **Repository**: `/Data/CampaignRepository.cs` (RavenDB logic).
- **Tests**: `/CampaignVault.Tests` (Functional test suite).
