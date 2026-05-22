# Security and Fly.io Deployment Plan

## Objective
Enhance the authentication mechanism of the D&D Campaign Vault MCP Server and prepare the project for a secure, persistent deployment on Fly.io, specifically optimized for integration with Grok's custom MCP connectors.

## Proposed Solution

### 1. Enhanced Authentication Middleware
Grok's custom connectors support standard HTTP headers for authentication. We will update the existing middleware in `Program.cs` to accept the secret token via:
- `Authorization: Bearer <TOKEN>` (Standard OAuth style)
- `X-API-Key: <TOKEN>` (Common API key style)
This ensures maximum compatibility with Grok's configuration UI.

### 2. Dockerization
Create a standard, multi-stage `Dockerfile` optimized for ASP.NET Core 10. This ensures a minimal footprint for the final image. We will also add a `.dockerignore` file to prevent copying local `bin/` and `obj/` folders.

### 3. Fly.io Configuration (`fly.toml`)
Create a `fly.toml` configuration file that defines the application setup. Crucially, this will include:
- **Persistent Volume:** A `[mounts]` section mapping a Fly.io volume to `/app/data`.
- **Environment Variables:** Setting `CAMPAIGN_DB_PATH` to `/app/data/campaign.db` to ensure LiteDB writes to the persistent volume.

### 4. Documentation
Update the `README.md` to include:
- Instructions on creating the Fly.io app and volume.
- Instructions on setting the secure secret (`fly secrets set BEARER_TOKEN=your_secret`).
- Deployment command (`fly deploy`).

## Phased Implementation Steps
1. Update `Program.cs` to handle `X-API-Key` alongside the existing `Bearer` token check.
2. Create `Dockerfile` and `.dockerignore`.
3. Create `fly.toml` with volume mounts.
4. Update `README.md` with Fly.io deployment instructions.