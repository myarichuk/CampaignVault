# CampaignVault Authoring App Design Specification

This document outlines the architecture, data schemas, sync mechanics, and user interface for the **CampaignVault Authoring App** (`CampaignVault.Authoring`), an Avalonia desktop client designed for human-agent collaborative world building.

---

## 1. Overview & Goals

The CampaignVault Authoring App serves as a human-friendly workspace control center to author, organize, validate, and synchronize TTRPG campaign packages. It allows humans to edit rich narrative prose while enabling LLM agents to inject structured simulation data.

### Goals:
* **Human & Agent Co-Authoring:** Provide a workspace format (Markdown + YAML Frontmatter) that is easily editable by DMs and readable/writable by LLM agents.
* **Direct Database Sync:** Use a secure, high-speed gRPC stream to synchronize campaign packages directly with the CampaignVault RavenDB database, bypassing the LLM context limits.
* **Local Agent Connectivity:** Host a local MCP (Model Context Protocol) server within the app so that external developer agents (Claude Code, Grok CLI, Antigravity) can safely edit campaign files in real-time.
* **In-App AI Seeding:** Provide an optional in-app generator powered by `Microsoft.Extensions.AI` for users with configured API keys.

---

## 2. Workspace File Format & C# Model Mapping

A campaign package is a local directory containing subfolders for each type of database entity. Files are written as Markdown `.md` documents with YAML frontmatter.

### Subfolder Structure:
```
campaigns/[campaign_name]/
├── npcs/
│   └── kael_the_bold.md
├── locations/
│   └── yawning_portal.md
├── factions/
│   └── harpers.md
└── quests/
    └── rescue_the_cleric.md
```

### Mapping Schema Examples

#### Characters (`npcs/kael_the_bold.md`)
* **YAML Frontmatter:** Maps to the properties of `CampaignVault.Models.Character`.
* **Markdown Body:** Maps to `Character.Notes`.

```markdown
---
$type: "character"
id: "kael_the_bold"
name: "Kael the Bold"
classLevel: "Fighter 4"
currentHp: 42
maxHp: 45
keepAlive: true
systemStats:
  ruleset: "pf2e"
  level: 4
  attackBonus: 8
psychology:
  wants:
    - "Slay the Goblin King"
  fears:
    - "Losing his family sword"
needs:
  currentNeeds:
    tiredness: 4
    hunger: 2
---
# Kael the Bold
Kael is a seasoned human fighter from the northern wastes. He bears a long scar across his left cheek.

## Appearance
Tall, clad in heavy chainmail, carrying a massive two-handed sword.
```

#### Locations (`locations/yawning_portal.md`)
* **YAML Frontmatter:** Maps to the properties of `CampaignVault.Models.Location`.
* **Markdown Body:** Maps to `Location.Description`.

```markdown
---
$type: "location"
id: "yawning_portal"
name: "The Yawning Portal"
type: "Building"
parentLocationId: "waterdeep_streets"
exits:
  - targetLocationId: "undermountain_level_1"
    name: "The Well Downward"
    distanceBand: "Near"
pointsOfInterest:
  - "Durnan's Bar"
  - "The massive center well"
visualTags:
  - "noisy"
  - "busy"
---
# The Yawning Portal
The famous tavern in Waterdeep containing the massive well that descends into Undermountain.
```

---

## 3. gRPC Synchronization Channel

Rather than exposing database ports or using heavy JSON REST endpoints, the client and server communicate via a bidirectional gRPC contract defined in `campaign_vault_sync.proto`.

```protobuf
syntax = "proto3";

option csharp_namespace = "CampaignVault.gRPC.Protos";

package campaignvault;

service CampaignSyncService {
  // Handshake with credentials, active system state and versions
  rpc InitializeSession (InitRequest) returns (InitResponse);

  // Retrieve lightweight catalog of all entities in a campaign for diffing
  rpc GetCampaignCatalog (CatalogRequest) returns (CatalogResponse);

  // Fetch details for specific entity IDs on-demand or during sync catch-up
  rpc GetEntities (EntityRequest) returns (stream CampaignEntity);

  // Push local modifications back to the CampaignVault engine
  rpc PushCampaignChanges (PushRequest) returns (PushResponse);

  // Subscribe to real-time simulation pressures emitted as time passes
  rpc StreamSimulationPressures (PressureRequest) returns (stream PressureUpdate);
}

message InitRequest {
  string auth_token = 1;
  string client_version = 2;
}

message InitResponse {
  bool success = 1;
  string message = 2;
  repeated string available_campaigns = 3;
  string active_campaign = 4;
}

message CatalogRequest {
  string campaign_name = 1;
}

message CatalogEntry {
  string entity_id = 1;
  string entity_type = 2;
  string name = 3;
  int64 last_updated_ticks = 4;
  string md5_hash = 5;
}

message CatalogResponse {
  repeated CatalogEntry entries = 1;
}

message EntityRequest {
  string campaign_name = 1;
  repeated string entity_ids = 2;
}

message CampaignEntity {
  string entity_id = 1;
  string entity_type = 2;
  string yaml_payload = 3;
  string markdown_body = 4;
}

message PushRequest {
  string campaign_name = 1;
  // Polymorphic WorldChanges array passed as a JSON string to leverage existing STJ converters
  string world_changes_json = 2; 
}

message PushResponse {
  bool success = 1;
  string error_message = 2;
  repeated string updated_entity_ids = 3;
}

message PressureRequest {
  string campaign_name = 1;
}

message PressureUpdate {
  string level = 1;
  string source = 2;
  string message = 3;
  string raw_commit_json = 4;
}
```

### Sync Lifecycle:
1. **Catalog Handshake:** Client requests the Catalog.
2. **Local Comparison:** Client checks timestamps and MD5 hashes of local files against remote entries.
3. **Lazy Retrieval:** If a remote entry has a newer timestamp (e.g. from an active session session modifying HP/moods), the client pulls the specific entity via `GetEntities` and updates the local markdown file.
4. **Conflict Resolution:** If both local and remote have modified since last sync, the client displays a side-by-side Diff View for the user to choose: **Pull Remote** or **Push Local**.

---

## 4. AI & Agent Integration

The app acts as a local gateway for agentic tools.

```
       ┌────────────────────────────────────────────────────────┐
       │                 AVALONIA AUTHORING APP                 │
       │                                                        │
       │  ┌───────────────────────┐   ┌──────────────────────┐  │
       │  │ In-Process MCP Server │   │ MS LLM SDK Interface │  │
       │  │ (SSE HTTP Server)     │   │ (Microsoft.Extensions│  │
       │  └───────────┬───────────┘   │  .AI Integration)    │  │
       │              │               └──────────┬───────────┘  │
       └──────────────┼──────────────────────────┼──────────────┘
                      ▼                          ▼
              [Local Workspace Files] ◄─── [API Credentials]
              (md / yaml campaign files)    (Gemini, OpenAI, Anthropic)
```

### 1. In-Process MCP Server (SSE Server)
The app runs a local HTTP server exposing a Model Context Protocol endpoint. 
* **Port Configuration:** Port is configurable in the application settings (defaulting to `8080`).
* **Exposed Tools:**
  * `list_workspace_entities()`: Return directory trees of current campaigns.
  * `read_workspace_entity(id, type)`: Return yaml frontmatter + body of a file.
  * `write_workspace_entity(id, type, yaml_frontmatter, markdown_body)`: Write or update a file.
  * `trigger_vault_sync()`: Trigger gRPC synchronization on demand.
* **Watcher Integration:** The app uses a C# `FileSystemWatcher` to detect when an external agent modifies workspace files, dynamically refreshing the UI view.

### 2. In-App LLM Generation (Microsoft.Extensions.AI)
An optional generative panel is included inside the UI:
* **Configuration:** Users can configure their endpoint provider (Gemini, OpenAI, Anthropic, Ollama) and enter their API key.
* **Optional UI State:** If no API provider/key is configured, all generation panels are disabled with a prompt advising configuration in the Settings tab.
* **Structured Generation:** Uses `IChatClient` with JSON Schema constraint formatting to ensure returned models strictly match database C# schemas.

---

## 5. UI Layout & ViewModels

The application uses the standard MVVM design pattern:

### Views:
* **Main Window:** Split into a Sidebar tree-view (campaign files) and a Central Content region.
* **Central Region Tabs:**
  1. **Workspace Editor:** A side-by-side text editor and live HTML preview. YAML values are parsed into structured "metadata badges" shown at the top of the preview panel.
  2. **Sync Diffs:** A grid indicating modified/added/deleted files with a side-by-side text diff viewer and a "Commit Changes via gRPC" action.
  3. **Settings:** Controls for gRPC (IP/Port/Token), MCP Server (Port/Status), and LLM Provider (Provider selection, Endpoint URL, API Key).
* **Generation Drawer:** A right-hand sidebar for entering text prompts (disabled unless LLM keys are supplied).

### ViewModels:
* `WorkspaceViewModel`: Directory indexing, search filtering, and `FileSystemWatcher` events.
* `EditorViewModel`: File buffering, YAML and Markdown parsing.
* `SyncViewModel`: gRPC connection management, catalog diffing, and streaming.
* `SettingsViewModel`: Handles configuration persistence (`campaign_authoring_settings.json`).

---

## 6. Implementation Stages

1. **Stage 1 (Shared Library & Parser):** Create the YAML/Markdown parsing engine and verify bidirectional mapping with CampaignVault C# models.
2. **Stage 2 (gRPC Integration):** Add the gRPC proto to the C# solution, implement the gRPC service on the CampaignVault server, and implement the gRPC client in the Avalonia app.
3. **Stage 3 (Workspace UI):** Build the Avalonia UI layout, directory tree loading, markdown preview, and settings views.
4. **Stage 4 (In-Process MCP Server):** Build the SSE HTTP MCP server to expose the workspace to external CLI agents.
5. **Stage 5 (In-App LLM Panel):** Wire up `Microsoft.Extensions.AI` and conditional disabling of the generation UI.
