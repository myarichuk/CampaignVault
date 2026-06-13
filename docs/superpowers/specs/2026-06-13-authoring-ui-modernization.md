# Design Spec: Authoring App UI Modernization

**Date:** 2026-06-13  
**Topic:** UI Overhaul for CampaignVault Authoring Application

## 1. Goal
Transform the CampaignVault Authoring App from a single-view utility into a professional desktop environment. The primary focus is on handling the full lifecycle of a campaign: Discovery, Selection, Creation, Syncing, and Authoring.

## 2. Architecture: Shell & Hub
The application will transition to a **State-Aware Shell** architecture using a `TransitioningContentControl` in the `MainWindow`.

### 2.1 The Shell (MainWindow)
*   **Main Menu Bar:** Standard desktop menu (File, Edit, View, Campaign, Help).
*   **Global Toolbar:** Quick access to Save, Open, New Entity, and a Global Sync Status widget.
*   **State Management:**
    *   `AppState.Idle`: Show `HubView`.
    *   `AppState.Editor`: Show `DockControl` with the campaign workspace loaded.

### 2.2 The Welcome Hub (HubView)
A centralized launchpad that handles campaign onboarding.
*   **Recent Campaigns:** A persistent list of locally-known campaigns with sync status indicators.
*   **Remote Discovery:** Automatically fetches the list of all campaigns from the configured CampaignVault (via gRPC).
*   **Cloud Onboarding:** Users can select a "Remote Only" campaign to "Stream to Local".

## 3. Workflows

### 3.1 Cloud-First "Stream to Local"
1.  User selects a campaign from the "Available on Vault" list in the Hub.
2.  User selects a local destination folder.
3.  **Sync Progress UI:** A modal progress overlay appears, showing real-time feedback as the app:
    *   Initializes the local `WorkspaceDb`.
    *   Fetches documents via gRPC `CampaignSyncService`.
    *   Writes local `.md` files to the filesystem.
4.  Once complete, the app transitions to the `AppState.Editor`.

### 3.2 Main Menu & Toolbar
*   **File Menu:** New Campaign, Open Campaign, Open Recent, Save All, Exit.
*   **Campaign Menu:** Open Sync Dashboard, Validate All Entities, Push All Changes.
*   **Global Sync Status:** A persistent widget in the toolbar/status bar showing:
    *   Connection Health (Colored indicator).
    *   Pending Changes Count (Local vs. Remote).
    *   "Sync Now" quick-action button.

## 4. Components & Data Flow

### 4.1 HubViewModel
*   Manages the `ObservableCollection` of local and remote campaigns.
*   Handles the "Sync Progress" state and commands.
*   Persists "Recent Campaigns" to a local `history.json` file.

### 4.2 ApplicationStateService
*   A centralized service to manage the transition between Hub and Editor states.
*   Ensures that resources (Watchers, DB connections) are properly disposed of when switching campaigns.

### 4.3 GlobalSyncService
*   Refactored from `SyncViewModel` to be a long-lived background service.
*   Polls/Monitors the remote Vault and local workspace to provide real-time diff counts to the Shell.

## 5. UI Layout (Aesthetics)
*   **Theme:** Modern dark theme (consistent with current #18181F background).
*   **Transitions:** Smooth cross-fades between Hub and Editor states.
*   **Feedback:** Rich progress bars and status text during all I/O operations.

## 6. Implementation Stages
1.  **Stage 1:** Shell Refactor (Add Menu, Toolbar, and State Switching logic).
2.  **Stage 2:** Hub Implementation (Recent list + Local folder opening).
3.  **Stage 3:** Cloud Discovery (Fetch remote list via gRPC).
4.  **Stage 4:** Sync Progress UI (Modal overlay + streaming downloader).
