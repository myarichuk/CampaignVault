# Implementation Plan: CampaignVault Authoring Workflow Redesign

This plan outlines the step-by-step tasks to rewrite the CampaignVault Authoring App workflow on the `master` branch.

---

## Task 1: Update gRPC Protocol & Server Campaign Sync Service

### Steps:
1. Update `src/CampaignVault/Protos/vault_sync.proto` with campaign-filtering APIs:
   - Add `GetCampaigns`, `GetCampaignEntities`, and `PushCampaignEntity`.
   - Update message contracts (`CampaignListResponse`, `CampaignItem`, `GetCampaignEntitiesRequest`, `PushCampaignEntityRequest`).
2. Update the gRPC server service in `src/CampaignVault/Services/CampaignSyncService.cs`:
   - Implement `GetCampaigns` to list all unique campaign names in RavenDB.
   - Implement `GetCampaignEntities` to filter `Character`, `Location`, and `Quest` documents by `CampaignName` matching the request.
   - Implement `PushCampaignEntity` to store the incoming entity with its `CampaignName` field explicitly set.
3. Rebuild the `CampaignVault` project to regenerate gRPC stubs.

### Verification:
- Compile `CampaignVault` and ensure it builds successfully.
- Verify both projects reference the updated gRPC contracts.

---

## Task 2: SQLite Local Index Manager in Authoring App

### Steps:
1. Add `Microsoft.Data.Sqlite` NuGet dependency to `CampaignVault.Authoring.csproj` if not already present.
2. Create `src/CampaignVault.Authoring/Services/WorkspaceDbService.cs`:
   - Setup initialization of the `.cv/index.db` SQLite file in the workspace directory.
   - Create tables `Entities` (Id, EntityType, RelativePath, FileHash, LastSyncedHash, SyncStatus, SchemaData) and `Relationships`.
   - Implement CRUD operations for entities and bulk status resets.

### Verification:
- Add a unit test or check logic that initializes a test database and verifies all tables exist and can write/read records.

---

## Task 3: Workspace Scanner & Local Indexer

### Steps:
1. Create `src/CampaignVault.Authoring/Services/WorkspaceScanner.cs`:
   - Recursively scan type-based folders (`characters/`, `locations/`, `quests/`, etc.) in the workspace root.
   - For each `.md` file, calculate its SHA-256 hash and parse its frontmatter.
   - Add or update the file record in the SQLite `Entities` table.
2. Update `WorkspaceViewModel.cs`:
   - Instead of listing all markdown files from direct disk enumeration, populate the `Files` collection from the SQLite database.
   - Support category groupings or category filtering based on `EntityType`.

### Verification:
- Open a workspace folder. Verify the `.cv/index.db` file is populated with the scanned markdown files and hashes.

---

## Task 4: Integrate Dock.Avalonia and AvaloniaEdit

### Steps:
1. Add `Dock.Avalonia` and `AvaloniaEdit` dependencies to `CampaignVault.Authoring.csproj`.
2. Rewrite `src/CampaignVault.Authoring/Views/MainWindow.axaml` and `MainWindowViewModel.cs`:
   - Set up `DockControl` with left panel (Explorer tree), center document area, right panel (AI Generator), and bottom panel (Sync Diff).
   - Configure the center document area to load `AvaloniaEdit` controls.
   - Create the split-screen view: Code pane (syntax highlighted markdown) and Form panel (stats binding).
3. Bind the fields in the Form panel to synchronize in real-time with the YAML frontmatter in the active document's `AvaloniaEdit` buffer.

### Verification:
- Launch the application and check that the docking system compiles, panel splits are adjustable, and the code editor loads correctly with YAML/Markdown content.

---

## Task 5: Git-Like Synchronization & Conflict Resolver

### Steps:
1. Update `src/CampaignVault.Authoring/ViewModels/SyncViewModel.cs` to implement:
   - **Fetch Campaign List:** Connect to server, retrieve list of active campaigns, and let the user select one during initialization.
   - **Fetch Updates:** Retrieve remote campaign entities, compare hashes against SQLite `LastSyncedHash` and `FileHash` to assign states (`ModifiedLocally`, `ModifiedRemotely`, `Conflict`).
   - **Stage & Push:** Push selected local additions or modifications to the server, updating `LastSyncedHash`.
   - **Pull:** Overwrite local markdown files with remote server state for unmodified local files.
2. Implement Conflict Resolution view in the UI:
   - Display side-by-side diff.
   - Provide buttons: "Keep Local" (push local to remote) or "Keep Remote" (pull remote to local file).

### Verification:
- Test sync flow end-to-end: edit a local file, trigger status check, staging, and push. Verify it commits correctly to the RavenDB server and sets state back to `Synced`.
