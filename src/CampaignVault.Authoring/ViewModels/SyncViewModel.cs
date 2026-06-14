using System;
using System.Security.Cryptography;
using System.Text;
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

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<string> _availableCampaigns = new();

    [ObservableProperty]
    private string? _selectedCampaign;

    public Func<CampaignSync.CampaignSyncClient>? ClientFactory { get; set; }

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

    internal CampaignSync.CampaignSyncClient CreateClient()
    {
        if (ClientFactory != null)
        {
            return ClientFactory();
        }
        var port = _settings.GrpcPortValue is > 0 and <= 65535
            ? (int)_settings.GrpcPortValue.Value
            : 50051;
        var token = string.IsNullOrWhiteSpace(_settings.GrpcToken) ? null : _settings.GrpcToken;
        return VaultGrpcClientFactory.CreateClient(_settings.GrpcHost, port, token);
    }

    [RelayCommand]
    internal async Task FetchCampaignsAsync()
    {
        StatusMessage = "Fetching campaign list from server...";
        try
        {
            var client = CreateClient();
            var response = await client.GetCampaignsAsync(new CampaignVault.Grpc.EmptyRequest());
            AvailableCampaigns.Clear();
            foreach (var c in response.Campaigns)
                AvailableCampaigns.Add(c.Name);

            // Auto-select campaign matching the workspace folder name
            var folderName = string.IsNullOrEmpty(_workspace.CurrentDirectory)
                ? null
                : System.IO.Path.GetFileName(_workspace.CurrentDirectory);

            SelectedCampaign = AvailableCampaigns
                .FirstOrDefault(c => string.Equals(c, folderName, System.StringComparison.OrdinalIgnoreCase))
                ?? AvailableCampaigns.FirstOrDefault();

            StatusMessage = AvailableCampaigns.Count > 0
                ? $"Found {AvailableCampaigns.Count} campaign(s). Selected: {SelectedCampaign}"
                : "No campaigns found on server.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to fetch campaigns: {ex.Message}";
        }
    }

    public bool CanPushSelected => SelectedDiff != null && 
        (SelectedDiff.Status == "AddedLocally" || SelectedDiff.Status == "ModifiedLocally");

    public bool CanPullSelected => SelectedDiff != null && 
        (SelectedDiff.Status == "AddedRemotely" || SelectedDiff.Status == "ModifiedRemotely");

    public bool IsConflictSelected => SelectedDiff != null && SelectedDiff.Status == "Conflict";

    partial void OnSelectedDiffChanged(SyncDiffItem? value)
    {
        OnPropertyChanged(nameof(CanPushSelected));
        OnPropertyChanged(nameof(CanPullSelected));
        OnPropertyChanged(nameof(IsConflictSelected));
    }

    public async Task PopulateActualDiffsAsync()
    {
        SyncDiffs.Clear();
        SelectedDiff = null;

        if (string.IsNullOrWhiteSpace(_workspace.CurrentDirectory))
        {
            StatusMessage = "No workspace directory opened.";
            return;
        }

        StatusMessage = "Scanning local workspace and remote catalog...";

        try
        {
            var campaignName = SelectedCampaign ?? Path.GetFileName(_workspace.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(campaignName))
            {
                StatusMessage = "No campaign selected. Use 'Fetch Campaigns' to discover server campaigns.";
                return;
            }
            var client = CreateClient();

            // 1. Fetch remote entities
            var remoteResponse = await client.GetCampaignEntitiesAsync(new GetCampaignEntitiesRequest { CampaignName = campaignName });
            var remoteEntities = remoteResponse.Entities;

            // 2. Fetch local entities from SQLite
            var localEntities = _workspace.DbService.GetAllEntities();

            // 3. Compare
            var allIds = localEntities.Select(e => e.Id)
                .Union(remoteEntities.Select(e => e.Id))
                .Distinct()
                .ToList();

            foreach (var id in allIds)
            {
                var local = localEntities.FirstOrDefault(e => e.Id == id);
                var remote = remoteEntities.FirstOrDefault(e => e.Id == id);

                if (remote == null && local != null)
                {
                    // Only exists locally
                    var absolutePath = Path.Combine(_workspace.CurrentDirectory, local.RelativePath);
                    if (File.Exists(absolutePath))
                    {
                        var content = await File.ReadAllTextAsync(absolutePath);
                        SyncDiffs.Add(new SyncDiffItem
                        {
                            FilePath = absolutePath,
                            FileName = Path.GetFileName(absolutePath),
                            Status = string.IsNullOrEmpty(local.LastSyncedHash) ? "AddedLocally" : "ModifiedLocally",
                            LocalContent = content,
                            RemoteContent = string.Empty,
                            EntityType = local.EntityType,
                            EntityId = id
                        });
                    }
                }
                else if (remote != null && local == null)
                {
                    // Only exists remotely
                    var remoteMarkdown = DeserializeRemoteToMarkdown(remote);
                    var relativePath = $"{remote.Type}s/{Path.GetFileName(remote.Id)}.md";
                    var absolutePath = Path.Combine(_workspace.CurrentDirectory, relativePath);

                    SyncDiffs.Add(new SyncDiffItem
                    {
                        FilePath = absolutePath,
                        FileName = Path.GetFileName(absolutePath),
                        Status = "AddedRemotely",
                        LocalContent = string.Empty,
                        RemoteContent = remoteMarkdown,
                        EntityType = remote.Type,
                        EntityId = id
                    });
                }
                else if (remote != null && local != null)
                {
                    // Exists in both
                    var absolutePath = Path.Combine(_workspace.CurrentDirectory, local.RelativePath);
                    if (File.Exists(absolutePath))
                    {
                        var localMarkdown = await File.ReadAllTextAsync(absolutePath);
                        var localHash = ComputeSha256Hash(localMarkdown);
                        var syncedHash = local.LastSyncedHash;

                        var remoteMarkdown = DeserializeRemoteToMarkdown(remote);
                        var remoteHash = ComputeSha256Hash(remoteMarkdown);

                        if (localHash != remoteHash)
                        {
                            string status;
                            if (localHash == syncedHash && remoteHash != syncedHash)
                            {
                                status = "ModifiedRemotely";
                            }
                            else if (localHash != syncedHash && remoteHash == syncedHash)
                            {
                                status = "ModifiedLocally";
                            }
                            else
                            {
                                status = "Conflict";
                            }

                            SyncDiffs.Add(new SyncDiffItem
                            {
                                FilePath = absolutePath,
                                FileName = Path.GetFileName(absolutePath),
                                Status = status,
                                LocalContent = localMarkdown,
                                RemoteContent = remoteMarkdown,
                                EntityType = local.EntityType,
                                EntityId = id
                            });
                        }
                    }
                }
            }

            if (SyncDiffs.Count > 0) SelectedDiff = SyncDiffs[0];
            StatusMessage = $"Connected to Campaign '{campaignName}'. Found {SyncDiffs.Count} unsynchronized entities.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"gRPC Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SyncAllAsync()
    {
        if (SyncDiffs.Count == 0) return;
        IsSyncing = true;
        StatusMessage = "Syncing all non-conflicting changes...";

        int pushedCount = 0;
        int pulledCount = 0;
        int conflictCount = 0;

        try
        {
            var itemsToSync = SyncDiffs.ToList();
            foreach (var diff in itemsToSync)
            {
                if (diff.Status == "Conflict")
                {
                    conflictCount++;
                    continue;
                }

                try
                {
                    if (diff.Status == "AddedLocally" || diff.Status == "ModifiedLocally")
                    {
                        await PushItemAsync(diff);
                        SyncDiffs.Remove(diff);
                        pushedCount++;
                    }
                    else if (diff.Status == "AddedRemotely" || diff.Status == "ModifiedRemotely")
                    {
                        await PullItemAsync(diff);
                        SyncDiffs.Remove(diff);
                        pulledCount++;
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error syncing {diff.FileName}: {ex.Message}";
                }
            }

            LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            StatusMessage = $"Sync completed. Pushed: {pushedCount}, Pulled: {pulledCount}, Conflicts: {conflictCount}.";
            SelectedDiff = SyncDiffs.FirstOrDefault();
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
    private async Task PushSelectedAsync()
    {
        if (SelectedDiff == null) return;
        IsSyncing = true;
        StatusMessage = $"Pushing {SelectedDiff.FileName} to remote...";

        try
        {
            await PushItemAsync(SelectedDiff);
            SyncDiffs.Remove(SelectedDiff);
            SelectedDiff = SyncDiffs.FirstOrDefault();
            StatusMessage = "Push successful.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Push failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task PullSelectedAsync()
    {
        if (SelectedDiff == null) return;
        IsSyncing = true;
        StatusMessage = $"Pulling {SelectedDiff.FileName} from remote...";

        try
        {
            await PullItemAsync(SelectedDiff);
            SyncDiffs.Remove(SelectedDiff);
            SelectedDiff = SyncDiffs.FirstOrDefault();
            StatusMessage = "Pull successful.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pull failed: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task ResolveKeepLocalAsync()
    {
        await PushSelectedAsync();
    }

    [RelayCommand]
    private async Task ResolveKeepRemoteAsync()
    {
        await PullSelectedAsync();
    }

    private async Task PushItemAsync(SyncDiffItem diff)
    {
        var campaignName = SelectedCampaign ?? Path.GetFileName(_workspace.CurrentDirectory);
        var client = CreateClient();

        var (json, entityType) = SerializeEntity(diff);
        if (json == null) throw new InvalidOperationException("Failed to parse local entity markdown.");

        var pushReq = new PushCampaignEntityRequest
        {
            CampaignName = campaignName,
            Id = diff.EntityId,
            Type = entityType,
            Content = json
        };
        var response = await client.PushCampaignEntityAsync(pushReq);
        if (!response.Success) throw new Exception(response.Message);

        var localHash = ComputeSha256Hash(diff.LocalContent);
        _workspace.DbService.UpdateLastSyncedHash(diff.EntityId, localHash, "Synced");

        await _workspace.RefreshLocalStateAsync();
    }

    private async Task PullItemAsync(SyncDiffItem diff)
    {
        var directory = Path.GetDirectoryName(diff.FilePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(diff.FilePath, diff.RemoteContent);

        string schemaData = "{}";
        try
        {
            if (diff.EntityType == "character")
                schemaData = JsonSerializer.Serialize(_parser.ParseCharacter(diff.RemoteContent));
            else if (diff.EntityType == "location")
                schemaData = JsonSerializer.Serialize(_parser.ParseLocation(diff.RemoteContent));
            else if (diff.EntityType == "quest")
                schemaData = JsonSerializer.Serialize(_parser.ParseQuest(diff.RemoteContent));
        }
        catch {}

        var remoteHash = ComputeSha256Hash(diff.RemoteContent);
        var relativePath = Path.GetRelativePath(_workspace.CurrentDirectory, diff.FilePath).Replace('\\', '/');
        _workspace.DbService.UpsertEntity(
            diff.EntityId,
            diff.EntityType,
            relativePath,
            remoteHash,
            remoteHash,
            "Synced",
            schemaData
        );

        _workspace.RefreshFilesList();
    }

    private (string? json, string entityType) SerializeEntity(SyncDiffItem diff)
    {
        return diff.EntityType switch
        {
            "location" => (JsonSerializer.Serialize(_parser.ParseLocation(diff.LocalContent)), "location"),
            "quest"    => (JsonSerializer.Serialize(_parser.ParseQuest(diff.LocalContent)), "quest"),
            _          => (JsonSerializer.Serialize(_parser.ParseCharacter(diff.LocalContent)), "character")
        };
    }

    private string DeserializeRemoteToMarkdown(EntityItem remote)
    {
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

        if (remote.Type == "character")
        {
            var c = JsonSerializer.Deserialize<Character>(remote.Content);
            if (c != null)
            {
                var copy = new Character
                {
                    Id = c.Id,
                    Name = c.Name,
                    ClassLevel = c.ClassLevel,
                    CurrentHp = c.CurrentHp,
                    MaxHp = c.MaxHp,
                    DistinctiveFeatures = c.DistinctiveFeatures,
                    CurrentAppearance = c.CurrentAppearance,
                    VisualTags = c.VisualTags,
                    KeepAlive = c.KeepAlive,
                    Schedule = c.Schedule,
                    CurrentLocationId = c.CurrentLocationId,
                    CurrentActivity = c.CurrentActivity,
                    Psychology = c.Psychology,
                    Social = c.Social,
                    Needs = c.Needs,
                    SystemStats = c.SystemStats,
                    LastUpdated = c.LastUpdated,
                    CampaignName = c.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{c.Notes ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }
        else if (remote.Type == "location")
        {
            var l = JsonSerializer.Deserialize<Location>(remote.Content);
            if (l != null)
            {
                var copy = new Location
                {
                    Id = l.Id,
                    Name = l.Name,
                    Type = l.Type,
                    ParentLocationId = l.ParentLocationId,
                    Exits = l.Exits,
                    PointsOfInterest = l.PointsOfInterest,
                    AmbientCrowd = l.AmbientCrowd,
                    LastVisitedDay = l.LastVisitedDay,
                    Metadata = l.Metadata,
                    CurrentState = l.CurrentState,
                    VisualTags = l.VisualTags,
                    DistinctiveFeatures = l.DistinctiveFeatures,
                    LastUpdated = l.LastUpdated,
                    ControllingFactionId = l.ControllingFactionId,
                    DangerModifier = l.DangerModifier,
                    CampaignName = l.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{l.Description ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }
        else if (remote.Type == "quest")
        {
            var q = JsonSerializer.Deserialize<Quest>(remote.Content);
            if (q != null)
            {
                var copy = new Quest
                {
                    Id = q.Id,
                    Title = q.Title,
                    GiverId = q.GiverId,
                    Objectives = q.Objectives,
                    OverallState = q.OverallState,
                    Category = q.Category,
                    Urgency = q.Urgency,
                    RelatedLocationIds = q.RelatedLocationIds,
                    RelatedFactionIds = q.RelatedFactionIds,
                    VisibleToCharacterIds = q.VisibleToCharacterIds,
                    DeadlineDay = q.DeadlineDay,
                    LastUpdatedDay = q.LastUpdatedDay,
                    LastUpdated = q.LastUpdated,
                    CampaignName = q.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{q.DmNotes ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }

        return string.Empty;
    }

    private string ComputeSha256Hash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        var sb = new StringBuilder();
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
