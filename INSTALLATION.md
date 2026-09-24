# Installation & Deployment

CampaignVault can run locally for development, or deployed to the cloud for remote play.

---

## Local Development

### Prerequisites
- Docker (optional but recommended)
- .NET 8 SDK (for running without Docker)
- A campaign directory with LLM config

### Quick Start with Docker

```bash
# Build the image
docker build -t campaignvault:latest -f Dockerfile .

# Run the server
docker run -p 5275:5275 \
  -e CAMPAIGN_DB_PATH=/app/data \
  -v campaign_data:/app/data \
  campaignvault:latest

# Test it
curl http://localhost:5275/health
```

Point your LLM to `http://localhost:5275` with no authentication.

### Without Docker (.NET CLI)

```bash
dotnet run
```

The server listens on `http://localhost:5275` by default.

---

## Remote Deployment

### Option 1: ngrok (Temporary / Testing)

Use ngrok to expose your local server for quick testing before permanent deployment.

```bash
# 1. Start your local server (see above)

# 2. Download and run ngrok
ngrok http 5275

# 3. ngrok prints a public URL like: https://abc123.ngrok.io

# 4. In your LLM connector, use that URL
```

**To add authentication:**

```bash
docker run -p 5275:5275 \
  -e BEARER_TOKEN=your-secure-token \
  -e CAMPAIGN_DB_PATH=/app/data \
  -v campaign_data:/app/data \
  campaignvault:latest
```

Then pass the token in LLM connector headers: `Authorization: Bearer your-secure-token`

---

### Option 2: Fly.io (Production)

Fly.io provides a simple, persistent deployment with auto-scaling and a free tier.

#### 1. Install Fly CLI

```bash
# macOS
brew install flyctl

# Linux / Windows: https://fly.io/docs/hands-on/install-flyctl/
```

#### 2. Create app and storage

```bash
fly apps create my-campaign-vault
fly volumes create campaign_data --region ams --size 1
```

Change region and app name as needed (common regions: `ams`, `iad`, `ord`, `syd`).

#### 3. Set authentication token

```bash
fly secrets set BEARER_TOKEN=your-secure-random-token
```

Generate a strong token: `openssl rand -base64 32`

#### 4. Deploy

The repo includes a `fly.toml` with sensible defaults. Update the `app` name to match your app:

```bash
fly deploy
```

#### 5. Your app is live

```
https://my-campaign-vault.fly.dev
```

Use this URL in your LLM connector with the token in headers.

---

### Option 3: Other Cloud Providers

Any Docker-capable host works (GCP Cloud Run, AWS ECS, DigitalOcean, etc.). The container runs on port `8080` in Docker and expects environment variables (see [Configuration](#configuration) below).

---

## Configuration

All configuration is via environment variables. No config files are needed for basic use.

| Variable | Purpose | Default |
|----------|---------|---------|
| `CAMPAIGN_DB_PATH` | RavenDB data directory | `{AppBase}/RavenData` |
| `BEARER_TOKEN` | Optional auth token (env only) | unset = no auth |
| `CORS_ALLOWED_ORIGINS` | Comma-separated origins, or `*` | `*` (allow any) |
| `MCP_PORT` | HTTP MCP + health listener port | `5275` (Fly: `8080`) |
| `MCP_BIND_ANY` | Bind `0.0.0.0` instead of `localhost` | `1` in Docker/Fly; `0` in local dev |
| `MCP_STDIO` | Enable stdio MCP transport | unset |
| `GRPC_PORT` | gRPC sync port for authoring UI | `50051` |

### Campaign Scoping

Every campaign-scoped MCP tool requires an explicit `campaignName` slug. There is no per-session or global campaign selection. Example:

```
/start_session campaignName="my-campaign"
/take_turn campaignName="my-campaign" changes=[...]
/get_entity campaignName="my-campaign" entityId="chars/grog"
```

---

## Authentication

Authentication is **optional** and only enabled when `BEARER_TOKEN` is set.

### Token Behavior

- If `BEARER_TOKEN` is **not set**, the server accepts all requests (convenient for local dev)
- If `BEARER_TOKEN` **is set**, all requests except `/` and `/health` must present a valid token
- Tokens are **case-sensitive** and compared using timing-safe comparison

### Supported Methods

The server checks for a valid token in this order:

#### 1. Authorization Header (Recommended)
```http
Authorization: Bearer your-secure-token-here
```

#### 2. X-API-Key Header
```http
X-API-Key: your-secure-token-here
```

#### 3. Query Parameter (Fallback)
```
https://your-app.example.com/?token=your-secure-token-here
```

⚠️ **Security Warning:** Query parameters are logged by servers, proxies, and analytics. Only use this method when headers are not available (e.g., some legacy LLM integrations).

### Recommendations by Environment

| Environment | Approach | Notes |
|---|---|---|
| Local dev | Don't set `BEARER_TOKEN` | Fastest iteration |
| Testing via ngrok | Low-privilege token or ngrok `--basic-auth` | Never use production token |
| Production | `Authorization: Bearer` header | Query parameter only as last resort |

### Best Practices

- Generate long, random tokens: `openssl rand -base64 32`
- Store tokens securely (environment variables, secrets managers, Fly.io secrets)
- Rotate tokens periodically
- Tokens are **case-sensitive** — store and use exactly
- For additional protection, use Cloudflare Access, Tailscale, or similar identity-aware proxies

---

## RavenDB Setup

CampaignVault uses embedded RavenDB for local/development deployments.

### Production (Fly.io)

The `fly.toml` mounts a persistent volume at `/app/data`. RavenDB stores the database there automatically.

### Local Development

RavenDB data is stored at `{CAMPAIGN_DB_PATH}/campaign.db` by default. The location can be customized via the `CAMPAIGN_DB_PATH` environment variable.

### Community License (Production)

RavenDB Community Edition is free for development and learning, but requires a free license key for production use.

1. Get a free key: https://ravendb.net/license/request/community
2. Follow RavenDB's setup docs to register it in your environment

For Fly.io deployments using embedded RavenDB, you may need to manage the license separately depending on scale. See [COMMERCIAL.md](./COMMERCIAL.md) for details.

---

## Troubleshooting

### "Docker daemon is not running"
Start Docker and try again.

### "campaignvault:latest image not found"
Run `docker build -t campaignvault:latest -f Dockerfile .` first.

### "Connection refused" / "Cannot connect to localhost:5275"
- Verify the container is running: `docker ps`
- Verify the port is exposed: `docker logs <container-id>`
- If using Fly.io, check: `fly status`

### "Unauthorized" (remote deployment)
- Verify `BEARER_TOKEN` is set: `fly secrets list`
- Verify the token is passed in headers or query string (see [Authentication](#authentication))
- Token is **case-sensitive** — verify exact match

### "RavenDB data not persisting"
- Docker: verify the volume mount: `docker inspect <container-id> | grep Mounts`
- Fly.io: verify the volume is attached: `fly volumes list`

### "Port already in use"
Change the port with `MCP_PORT` environment variable:
```bash
docker run -p 8000:8000 -e MCP_PORT=8000 campaignvault:latest
```

---

## Performance & Scaling

### Local Development
Embedded RavenDB is single-process and sufficient for development. The server handles typical campaign workloads (100s of NPCs, 1000s of events) without issue.

### Production (Fly.io)
- Use a persistent volume (1GB is plenty for most campaigns)
- Monitor disk usage: `fly volumes list`
- RavenDB is embedded; concurrency is handled via transaction retries (see `CampaignTools.ExecuteAsync` in the code)

### Multi-Campaign Deployments
The engine supports multiple campaigns in one RavenDB instance. Isolation is enforced at the application layer (campaign names are canonicalized and queries filtered accordingly).

---

## Connecting LLMs

### Generic HTTP Connector

Most LLMs support pointing to a custom HTTP MCP server:

1. Start CampaignVault (local or remote)
2. In your LLM client, add a custom connector:
   - **URL:** `http://localhost:5275` (local) or `https://my-campaign-vault.fly.dev` (remote)
   - **Auth method:** None (local) or Bearer token (remote)
   - **Port:** 5275 (local) or 443 (remote)

### Claude.ai

See the [System Prompt](./recommended-system-prompt.md) for instructions on configuring CampaignVault as a custom MCP tool.

### opencode

CampaignVault ships with a dedicated opencode plugin and setup script:

```bash
./scripts/setup-opencode.sh /path/to/campaign \
  --slug my-campaign \
  --ruleset Dnd5e \
  --roster "chars/valen,chars/nia" \
  --mcp-port 5275
```

(Assumes the MCP server is already running on port 5275.)

See [README.md](./README.md#opencode-integration) for details.

---

## Development & Debugging

### Local server with logging

```bash
dotnet run
```

The server logs HTTP requests, tool calls, and simulation events to stdout.

### Grpc authoring UI (internal)

If you're developing the authoring UI, it connects via gRPC on port `50051` (or `GRPC_PORT`). The MCP HTTP server and gRPC server run side-by-side in the same process.

---

## Next Steps

1. **Start locally** — Get familiar with campaign creation and session flow
2. **Read the system prompt** — Understand how to guide the LLM for best results
3. **Call `lookup kind=help`** — Inside a campaign, the built-in DM manual has patterns and examples
4. **Deploy to Fly.io** — When ready for persistent, remote play
