using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Grpc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;

namespace CampaignVault.Authoring.ViewModels;

public partial class SyncDiffItem : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _status = "Modified"; // Modified, Added Remote, Added Local, Deleted

    [ObservableProperty]
    private string _localContent = string.Empty;

    [ObservableProperty]
    private string _remoteContent = string.Empty;

    [ObservableProperty]
    private bool _isSynchronized;
    
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
}

public partial class SyncViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private readonly WorkspaceViewModel _workspace;
    private readonly WorkspaceParser _parser = new();

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _statusMessage = "Disconnected from CampaignVault Remote.";

    [ObservableProperty]
    private string _lastSyncTime = "Never";

    [ObservableProperty]
    private ObservableCollection<SyncDiffItem> _syncDiffs = new();

    [ObservableProperty]
    private SyncDiffItem? _selectedDiff;

    [ObservableProperty]
    private bool _isSyncing;

    public SyncViewModel(SettingsViewModel settings, WorkspaceViewModel workspace)
    {
        _settings = settings;
        _workspace = workspace;
    }

    public void ClearDiffs()
    {
        SyncDiffs.Clear();
        SelectedDiff = null;
        StatusMessage = "Disconnected from CampaignVault gRPC sync.";
    }

    public void UpdateConnectionStatus(string message)
    {
        StatusMessage = message;
    }

    private CampaignSync.CampaignSyncClient CreateClient()
    {
        var port = _settings.GrpcPortValue is > 0 and <= 65535
            ? (int)_settings.GrpcPortValue.Value
            : 50051;
        var token = string.IsNullOrWhiteSpace(_settings.GrpcToken) ? null : _settings.GrpcToken;
        return VaultGrpcClientFactory.CreateClient(_settings.GrpcHost, port, token);
    }

    public async Task PopulateActualDiffsAsync()
    {
        SyncDiffs.Clear();
        StatusMessage = "Connected. Scanning local workspace and remote catalog...";

        try
        {
            var client = CreateClient();
            var response = await client.GetCampaignEntitiesAsync(new GetCampaignEntitiesRequest { CampaignName = "default" });

            // Simple mockup: just list everything local as Added Local
            // If we actually compare, we would parse local and compare JSON to Remote JSON.
            // For now, let's just create diff items for Remote items.
            foreach (var remoteEntity in response.Entities)
            {
                // Attempt to find local match by ID (ignoring folders for now)
                var localFile = _workspace.Files.FirstOrDefault(f => f.FileName.Contains(remoteEntity.Id));

                string localContent = string.Empty;
                string status = "Added Remote";
                if (localFile != null && File.Exists(localFile.FilePath))
                {
                    localContent = await File.ReadAllTextAsync(localFile.FilePath);
                    status = "Modified"; // Simplified comparison
                }

                SyncDiffs.Add(new SyncDiffItem
                {
                    FilePath = localFile?.FilePath ?? $"{remoteEntity.Type}s/{remoteEntity.Id}.md",
                    FileName = localFile?.FileName ?? $"{remoteEntity.Id}.md",
                    Status = status,
                    LocalContent = localContent,
                    RemoteContent = remoteEntity.Content, // Display raw JSON from remote
                    EntityType = remoteEntity.Type,
                    EntityId = remoteEntity.Id
                });
            }

            // Also add local files that are not in remote
            foreach (var localFile in _workspace.Files)
            {
                if (!SyncDiffs.Any(d => d.FilePath == localFile.FilePath))
                {
                    var content = await File.ReadAllTextAsync(localFile.FilePath);
                    try {
                        var parsed = _parser.ParseCharacter(content); // Try parse to get ID
                        SyncDiffs.Add(new SyncDiffItem
                        {
                            FilePath = localFile.FilePath,
                            FileName = localFile.FileName,
                            Status = "Added Local",
                            LocalContent = content,
                            RemoteContent = string.Empty,
                            EntityType = "character", // default
                            EntityId = parsed.Id
                        });
                    } catch {}
                }
            }

            if (SyncDiffs.Count > 0) SelectedDiff = SyncDiffs[0];
            StatusMessage = $"Connected. Found {SyncDiffs.Count} unsynchronized entities.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"gRPC Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SyncAllAsync()
    {
        if (!IsConnected || SyncDiffs.Count == 0) return;

        IsSyncing = true;
        StatusMessage = "Syncing changes via gRPC Sync Channel...";

        try
        {
            var client = CreateClient();
            foreach (var diff in SyncDiffs.ToList())
            {
                if (diff.Status == "Added Local" || diff.Status == "Modified")
                {
                    // Push local to remote, serialising as the correct entity type
                    try
                    {
                        var (json, entityType) = SerializeEntity(diff);
                        if (json != null)
                        {
                             var pushReq = new PushCampaignEntityRequest { CampaignName = "default", Id = diff.EntityId, Type = entityType, Content = json };
                             await client.PushCampaignEntityAsync(pushReq);
                        }
                    }
                    catch { /* silently skip malformed local files */ }
                }
                diff.IsSynchronized = true;
            }

            SyncDiffs.Clear();
            SelectedDiff = null;
            LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            StatusMessage = "Synchronization complete! Workspace is up-to-date.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task SyncSelectedAsync()
    {
        if (!IsConnected || SelectedDiff == null) return;

        IsSyncing = true;
        StatusMessage = $"Syncing {SelectedDiff.FileName} via gRPC...";

        try
        {
            var diff = SelectedDiff;
            var client = CreateClient();

            if (diff.Status == "Added Local" || diff.Status == "Modified")
            {
                var (json, entityType) = SerializeEntity(diff);
                if (json != null)
                {
                    var pushReq = new PushCampaignEntityRequest { CampaignName = "default", Id = diff.EntityId, Type = entityType, Content = json };
                    await client.PushCampaignEntityAsync(pushReq);
                }
            }

            diff.IsSynchronized = true;
            SyncDiffs.Remove(diff);
            
            if (SyncDiffs.Count > 0)
            {
                SelectedDiff = SyncDiffs[0];
                StatusMessage = $"Sync complete. {SyncDiffs.Count} items remaining.";
            }
            else
            {
                SelectedDiff = null;
                StatusMessage = "Sync complete. All items synchronized.";
                LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Parses a local diff item's content into the correct model type based on
    /// <see cref="SyncDiffItem.EntityType"/> and serialises it back to JSON for the gRPC push.
    /// Returns (null, type) when parsing fails so callers can skip gracefully.
    /// </summary>
    private (string? json, string entityType) SerializeEntity(SyncDiffItem diff)
    {
        return diff.EntityType switch
        {
            "location" => (JsonSerializer.Serialize(_parser.ParseLocation(diff.LocalContent)), "location"),
            "quest"    => (JsonSerializer.Serialize(_parser.ParseQuest(diff.LocalContent)), "quest"),
            _          => (JsonSerializer.Serialize(_parser.ParseCharacter(diff.LocalContent)), "character")
        };
    }
}
