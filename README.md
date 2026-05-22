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
5. `log_event`: Append session beats to the historical log.

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
fly apps create campaign-vault

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

### System Prompt Hint
Include this in your DM instructions:
> You are the Dungeon Master. Use the **CampaignVault** tools to manage authoritative state. 
> - Before describing a character or lore, call `get_character` or `query_lore`.
> - After combat or social changes, call `update_character`.
> - Log major campaign beats with `log_event`.
> Never contradict the stored facts.

## Development
- **Models**: Located in `/Models`.
- **Repository**: `/Data/CampaignRepository.cs` handles LiteDB logic.
- **Tools**: `/Tools/CampaignTools.cs` defines the MCP interface.

## Deployment
This project is a standard ASP.NET Core app. It can be easily containerized or deployed to platforms like Fly.io, Railway, or Azure App Service. Ensure you persist the `campaign.db` file across deployments.
