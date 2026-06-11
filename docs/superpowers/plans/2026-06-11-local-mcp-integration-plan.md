# CampaignVault Authoring App - Phase 2 (Local MCP Integration & AI Generation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate a local, in-process MCP SSE (Server-Sent Events) HTTP server, settings configuration (for port and LLM API credentials), workspace filesystem watching for external agent edits, and optional in-app AI generation powered by `Microsoft.Extensions.AI` (conditionally disabled if credentials are not set).

**Architecture:**
1. A configuration class `CampaignAuthoringSettings` stored in `campaign_authoring_settings.json`.
2. A `SettingsViewModel` and UI tab to configure settings.
3. A background ASP.NET Core Kestrel server running `ModelContextProtocol.AspNetCore` SSE transport on the configured port, exposing workspace manipulation tools.
4. A `FileSystemWatcher` in `WorkspaceViewModel` to reload the UI automatically when external agents write to workspace files.
5. A `GenerationViewModel` and UI panel using `IChatClient` from `Microsoft.Extensions.AI` to generate campaign entities, disabled when no credentials are configured.

**Tech Stack:** C#, Avalonia UI v11, Microsoft.AspNetCore.App (Kestrel), ModelContextProtocol.AspNetCore, Microsoft.Extensions.AI, YamlDotNet, xUnit

---

### Task 1: Add Dependencies & Settings Configuration Service

**Files:**
- Modify: `src/CampaignVault.Authoring/CampaignVault.Authoring.csproj`
- Create: `src/CampaignVault.Authoring/Models/CampaignAuthoringSettings.cs`
- Create: `src/CampaignVault.Authoring/Services/SettingsService.cs`
- Test: `tests/CampaignVault.Tests/Authoring/SettingsServiceTests.cs`

- [ ] **Step 1: Add dependencies to project file**
  Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `CampaignVault.Authoring.csproj` under the property group or in an ItemGroup to enable hosting Kestrel.
  Add NuGet package references:
  - `ModelContextProtocol.AspNetCore`
  - `Microsoft.Extensions.AI`
  - `Microsoft.Extensions.AI.Ollama` (optional, or construct generic OpenAI client for ollama/openai/gemini)
  
- [ ] **Step 2: Create Settings model**
  Define `CampaignAuthoringSettings` holding `McpPort`, `LlmProvider` ("None", "Ollama", "OpenAI", "Gemini"), `LlmApiKey`, `LlmEndpoint`, and `LlmModel`.
  
- [ ] **Step 3: Create Settings Service**
  Implement `SettingsService` that loads/saves configuration from a JSON file `campaign_authoring_settings.json` in the user's local AppData or base directory.
  
- [ ] **Step 4: Write and run unit tests for settings storage**
  Verify that settings serialize/deserialize correctly.
  
- [ ] **Step 5: Commit**
  `git commit -m "feat: add settings storage and service configuration"`

---

### Task 2: Implement Settings UI & ViewModels

**Files:**
- Modify: `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CampaignVault.Authoring/Views/MainWindow.axaml`
- Create: `src/CampaignVault.Authoring/ViewModels/SettingsViewModel.cs`

- [ ] **Step 1: Implement SettingsViewModel**
  Expose settings properties (McpPort, LlmProvider, LlmApiKey, LlmEndpoint, LlmModel) with MVVM notifications. Bind Save commands to persist settings.
  
- [ ] **Step 2: Update MainWindow to support tabs**
  Change `MainWindow.axaml` from a simple grid to a `TabControl` containing three tabs:
  - **Workspace Editor** (containing the existing Sidebar + TextBox)
  - **Settings** (fields for port, LLM credentials, and Save button)
  
- [ ] **Step 3: Verify build passes and UI loads settings**
  Build the project and run it to verify settings tab displays.
  
- [ ] **Step 4: Commit**
  `git commit -m "feat: implement settings UI and ViewModel"`

---

### Task 3: Implement In-Process SSE MCP Server

**Files:**
- Create: `src/CampaignVault.Authoring/Services/McpServerService.cs`
- Create: `src/CampaignVault.Authoring/Tools/AuthoringMcpTools.cs`
- Modify: `src/CampaignVault.Authoring/App.axaml.cs`
- Test: `tests/CampaignVault.Tests/Authoring/McpServerTests.cs`

- [ ] **Step 1: Define Authoring MCP Tools**
  Create `AuthoringMcpTools` class and declare the four required MCP tools:
  - `list_workspace_entities()`: Return campaign file hierarchy.
  - `read_workspace_entity(id, type)`: Parse markdown frontmatter/body.
  - `write_workspace_entity(id, type, yaml_frontmatter, markdown_body)`: Write markdown files.
  - `trigger_vault_sync()`: Stub gRPC synchronization.
  
- [ ] **Step 2: Implement McpServerService**
  Start an in-process Kestrel host running ASP.NET Core with `ModelContextProtocol.AspNetCore` SSE mapping (`app.MapMcp("/")`). Support starting, stopping, and restarting the server dynamically if the port is changed in settings.
  
- [ ] **Step 3: Integrate McpServerService into App lifecycle**
  Initialize the service on app start.
  
- [ ] **Step 4: Verify MCP server tools with basic tests**
  Write tests to call the MCP server or its direct tool handlers.
  
- [ ] **Step 5: Commit**
  `git commit -m "feat: implement in-process SSE MCP server for workspace access"`

---

### Task 4: Implement FileSystemWatcher for Workspace Auto-Refresh

**Files:**
- Modify: `src/CampaignVault.Authoring/ViewModels/WorkspaceViewModel.cs`
- Modify: `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`

- [ ] **Step 1: Add FileSystemWatcher to WorkspaceViewModel**
  Setup `FileSystemWatcher` on the loaded workspace directory. Listen to `Changed`, `Created`, and `Deleted` events.
  
- [ ] **Step 2: Trigger UI reload on file system events**
  Ensure events execute on the UI thread (`Dispatcher.UIThread.InvokeAsync`) to reload the files list and the currently open file if it was edited by an external agent.
  
- [ ] **Step 3: Verify workspace auto-refresh**
  Ensure it updates UI views when external agents rewrite files.
  
- [ ] **Step 4: Commit**
  `git commit -m "feat: integrate FileSystemWatcher for live UI updates"`

---

### Task 5: Implement In-App LLM Generation Panel

**Files:**
- Modify: `src/CampaignVault.Authoring/Views/MainWindow.axaml`
- Modify: `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`
- Create: `src/CampaignVault.Authoring/ViewModels/GenerationViewModel.cs`

- [ ] **Step 1: Implement GenerationViewModel using Microsoft.Extensions.AI**
  Check the configured `LlmProvider` and API credentials. If `None` or credentials missing, set `IsEnabled = false`.
  If enabled, create the appropriate `IChatClient` (e.g. `OpenAIChatClient` targeting OpenAI or Ollama endpoint, or custom wrapper) and handle user prompts to generate YAML frontmatter and Markdown body.
  
- [ ] **Step 2: Assemble Generation UI Drawer/Tab**
  Add a Generation drawer (right-hand sidebar) or Tab. Connect it to the `GenerationViewModel`. Disable the panel controls with a notice if `IsEnabled = false`.
  
- [ ] **Step 3: Verify generative client creation & UI state**
  Verify the generation panel displays a prompt advising settings configuration if credentials are not set, and enables inputs if keys are provided.
  
- [ ] **Step 4: Commit**
  `git commit -m "feat: implement optional in-app AI generation panel"`
