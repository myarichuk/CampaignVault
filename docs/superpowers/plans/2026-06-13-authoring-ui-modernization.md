# Authoring App UI Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform the CampaignVault Authoring App into a professional desktop environment with a Welcome Hub, Main Menu, Toolbar, and Cloud-First campaign discovery.

**Architecture:** A State-Aware Shell refactor of `MainWindow`. It will switch between a `HubView` (launchpad) and the `DockControl` (editor) using an `AppStateService`.

**Tech Stack:** Avalonia UI, CommunityToolkit.Mvvm, Dock.Avalonia, gRPC (for sync).

---

### Task 1: Foundation - AppStateService and Shell Switching

**Files:**
- Create: `src/CampaignVault.Authoring/Services/AppStateService.cs`
- Modify: `src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs`
- Modify: `src/CampaignVault.Authoring/Views/MainWindow.axaml`

- [ ] **Step 1: Create AppStateService**

```csharp
namespace CampaignVault.Authoring.Services;

public enum AppState { Idle, Editor }

public partial class AppStateService : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private AppState _currentState = AppState.Idle;
}
```

- [ ] **Step 2: Update MainWindowViewModel to handle state**

```csharp
// Add to MainWindowViewModel.cs
[ObservableProperty]
private AppStateService _appState = new();

// Command to open a campaign and switch state
[RelayCommand]
public void LoadCampaign(string path)
{
    Workspace.LoadDirectory(path);
    AppState.CurrentState = AppState.Editor;
}
```

- [ ] **Step 3: Refactor MainWindow.axaml for Shell Switching**

```xml
<!-- Wrap existing Grid in a TransitioningContentControl -->
<TransitioningContentControl Content="{Binding AppState.CurrentState}">
    <TransitioningContentControl.DataTemplates>
        <DataTemplate DataType="{x:Type services:AppState}" x:Key="Idle">
             <views:HubView />
        </DataTemplate>
        <DataTemplate DataType="{x:Type services:AppState}" x:Key="Editor">
             <!-- Move the existing Editor Grid here -->
        </DataTemplate>
    </TransitioningContentControl.DataTemplates>
</TransitioningContentControl>
```

- [ ] **Step 4: Commit**

```bash
git add src/CampaignVault.Authoring/Services/AppStateService.cs src/CampaignVault.Authoring/ViewModels/MainWindowViewModel.cs src/CampaignVault.Authoring/Views/MainWindow.axaml
git commit -m "feat: implement shell state switching foundation"
```

### Task 2: Main Menu and Toolbar

**Files:**
- Modify: `src/CampaignVault.Authoring/Views/MainWindow.axaml`

- [ ] **Step 1: Add Menu and Toolbar to MainWindow.axaml**

```xml
<DockPanel>
    <Menu DockPanel.Dock="Top">
        <MenuItem Header="_File">
            <MenuItem Header="_New Campaign..." />
            <MenuItem Header="_Open Folder..." Command="{Binding OpenCampaignFolderCommand}" />
            <Separator />
            <MenuItem Header="E_xit" />
        </MenuItem>
        <MenuItem Header="_Campaign" IsEnabled="{Binding AppState.CurrentState, Converter={StaticResource EnumToBoolConverter}, ConverterParameter=Editor}">
            <MenuItem Header="_Sync Dashboard" />
            <MenuItem Header="_Push All Changes" />
        </MenuItem>
    </Menu>
    
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Background="#2D2D3A" Spacing="10" Padding="5">
        <Button Content="Open" Command="{Binding OpenCampaignFolderCommand}" />
        <Button Content="Save All" Command="{Binding SaveActiveFileCommand}" />
    </StackPanel>

    <!-- Content goes here -->
</DockPanel>
```

- [ ] **Step 2: Commit**

```bash
git add src/CampaignVault.Authoring/Views/MainWindow.axaml
git commit -m "feat: add main menu and toolbar to shell"
```

### Task 3: HubView and Campaign History

**Files:**
- Create: `src/CampaignVault.Authoring/Services/CampaignHistoryService.cs`
- Create: `src/CampaignVault.Authoring/ViewModels/HubViewModel.cs`
- Create: `src/CampaignVault.Authoring/Views/HubView.axaml`

- [ ] **Step 1: Create CampaignHistoryService**

```csharp
public class CampaignHistory
{
    public List<string> RecentPaths { get; set; } = new();
}

public class CampaignHistoryService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampaignVault", "history.json");

    public CampaignHistory Load() { /* implementation */ }
    public void Add(string path) { /* implementation */ }
}
```

- [ ] **Step 2: Implement HubViewModel**

```csharp
public partial class HubViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<string> _recentCampaigns = new();

    [RelayCommand]
    private void OpenCampaign(string path) { /* calls MainWindowViewModel.LoadCampaign */ }
}
```

- [ ] **Step 3: Create HubView UI**

```xml
<UserControl ...>
    <Grid ColumnDefinitions="*, 300">
        <StackPanel Grid.Column="0">
            <TextBlock Text="Recent Campaigns" FontSize="20" FontWeight="Bold" />
            <ItemsControl ItemsSource="{Binding RecentCampaigns}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Button Content="{Binding}" Command="{Binding $parent[UserControl].DataContext.OpenCampaignCommand}" CommandParameter="{Binding}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
        <StackPanel Grid.Column="1" Background="#1C1C24" Padding="20">
            <Button Content="Create New Campaign" />
            <Button Content="Open Local Folder" Command="{Binding $parent[Window].DataContext.OpenCampaignFolderCommand}" />
        </StackPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 4: Commit**

```bash
git add src/CampaignVault.Authoring/Services/CampaignHistoryService.cs src/CampaignVault.Authoring/ViewModels/HubViewModel.cs src/CampaignVault.Authoring/Views/HubView.axaml
git commit -m "feat: implement Welcome Hub and campaign history"
```

### Task 4: Cloud Discovery via gRPC

**Files:**
- Modify: `src/CampaignVault.Authoring/ViewModels/HubViewModel.cs`
- Modify: `src/CampaignVault.Authoring/Services/CampaignSyncService.cs`

- [ ] **Step 1: Update HubViewModel to fetch remote campaigns**

```csharp
// In HubViewModel.cs
public async Task RefreshRemoteCampaigns()
{
    var remoteList = await _syncService.ListCampaignsAsync();
    // Update UI list with remote badges
}
```

- [ ] **Step 2: Commit**

```bash
git add src/CampaignVault.Authoring/ViewModels/HubViewModel.cs
git commit -m "feat: integrate remote campaign discovery into Hub"
```

### Task 5: Sync Progress UI and Downloader

**Files:**
- Create: `src/CampaignVault.Authoring/Views/SyncProgressDialog.axaml`
- Modify: `src/CampaignVault.Authoring/ViewModels/HubViewModel.cs`

- [ ] **Step 1: Create SyncProgressDialog UI**

```xml
<Window Title="Syncing Campaign..." WindowStartupLocation="CenterOwner" Width="400" Height="150">
    <StackPanel Margin="20" Spacing="10">
        <TextBlock Text="{Binding StatusText}" />
        <ProgressBar Value="{Binding ProgressValue}" Minimum="0" Maximum="100" Height="20" />
    </StackPanel>
</Window>
```

- [ ] **Step 2: Implement "Stream to Local" in HubViewModel**

```csharp
[RelayCommand]
public async Task DownloadRemoteCampaign(string campaignId)
{
    // 1. Show Progress Dialog
    // 2. Fetch all docs from gRPC
    // 3. Write to local disk
    // 4. Open campaign
}
```

- [ ] **Step 3: Commit**

```bash
git add src/CampaignVault.Authoring/Views/SyncProgressDialog.axaml src/CampaignVault.Authoring/ViewModels/HubViewModel.cs
git commit -m "feat: implement sync progress UI and campaign downloader"
```
