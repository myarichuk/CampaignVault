# D&D Campaign Vault - MCP Server Prototype

A minimal, reliable Model Context Protocol (MCP) server for managing D&D 5e campaign state. Designed to give Grok (or any LLM) structured, persistent access to characters, lore, and session events.

## Features
- **Persistent State**: Powered by LiteDB (single-file serverless database).
- **Flexible Documents**: Supports core D&D fields with the ability to add arbitrary dynamic data.
- **LLM-Optimized Tools**: Returns machine-readable data and human-friendly summaries.
- **Secure**: Optional Bearer token authentication.

## Core Tools
1. `get_character`: Retrieve full character details.
2. `upsert_character`: Create or fully replace a character sheet.
3. `update_character`: Partial updates (HP, status, notes).
4. `query_lore`: Search campaign world info by tags/keywords.
5. `upsert_lore`: Create or update lore (NPCs, locations, history).
6. `log_event`: Append session beats to the historical log.
7. `query_events`: Retrieve recent in-game history.

## Getting Started

### Prerequisites
- .NET 10 SDK (or compatible latest)

### Configuration
Environment variables or `appsettings.json`:
- `CAMPAIGN_DB_PATH`: Path to the `.db` file (default: `campaign.db`).
- `BEARER_TOKEN`: Optional secret token for authentication.

### Running Locally
```bash
dotnet run
```
The server will start (usually on `http://localhost:5000` or `5001`). 
The MCP endpoint is mapped to `/mcp`.

## Deployment to Fly.io

This project is optimized for deployment on [Fly.io](https://fly.io/).

### 1. Create the App and Volume
```bash
# Create the app
fly apps create my-campaign-vault-for-grok

# Create a 1GB persistent volume for LiteDB
fly volumes create campaign_data --region ams --size 1
```

### 2. Set Security Secrets
Set your `BEARER_TOKEN` (used for both Bearer and X-API-Key auth):
```bash
fly secrets set BEARER_TOKEN=your_secure_random_token
```

### 3. Deploy
```bash
fly deploy
```

## Grok-Side Setup

In your Grok custom connector configuration:
1. **URL**: `https://your-app-name.fly.dev/mcp`
2. **Authentication**: 
   - Add a header `Authorization` with value `Bearer your_secure_random_token`
   - OR add a header `X-API-Key` with value `your_secure_random_token`

### DM System Prompt for Grok
Add this to your DM instructions to ensure Grok uses the tools correctly:

> You are the Dungeon Master for our D&D 5e campaign. You have access to the **CampaignVault** tools to maintain a persistent, authoritative world state. 
> 
> **Core Directives:**
> 1. **authoritative State**: Never invent character stats or historical facts that contradict the Vault. If unsure, call `get_character` or `query_lore`.
> 2. **Continuous Updates**: 
>    - Call `update_character` whenever HP, status, or relationships change.
>    - Call `upsert_lore` when you introduce new NPCs, locations, or world facts.
>    - Call `log_event` at the end of every major scene or combat to record the "history" of the world.
> 3. **Session Prep**: At the start of a session, call `query_events` and `get_character` for all PCs to catch up on the current situation.
> 4. **Lore Richness**: Use `query_lore` with tags (e.g., `location`, `faction`) to find connected world details before describing a new area.
> 
> Your goal is to keep the Vault so accurate that any other DM (or another instance of yourself) could take over the campaign seamlessly.

## Development
- **Models**: Located in `/Models`.
- **Repository**: `/Data/CampaignRepository.cs` handles LiteDB logic.
- **Tools**: `/Tools/CampaignTools.cs` defines the MCP interface.

## Deployment
This project is a standard ASP.NET Core app. It can be easily containerized or deployed to platforms like Fly.io, Railway, or Azure App Service. Ensure you persist the `campaign.db` file across deployments.
