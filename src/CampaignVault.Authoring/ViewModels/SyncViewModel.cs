using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Grpc;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class VaultSyncPlanItem : ObservableObject
{
    public VaultEntitySyncPlan Plan { get; }

    [ObservableProperty] private string _displayName = string.Empty;

    [ObservableProperty] private string _localContent = string.Empty;

    [ObservableProperty] private string _remoteContent = string.Empty;

    [ObservableProperty] private string _mergedContent = string.Empty;

    public VaultSyncPlanItem(VaultEntitySyncPlan plan, string displayName)
    {
        Plan = plan;
        DisplayName = displayName;
    }
}

public partial class SyncViewModel : ObservableObject
{
    private readonly SettingsViewModel _settings;
    private CampaignVaultSession? _session;
    private Action? _refreshExplorer;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _statusMessage = "Open a vault and fetch to compare with Campaign Vault.";

    [ObservableProperty] private string _lastSyncTime = "Never";

    [ObservableProperty] private ObservableCollection<VaultSyncPlanItem> _syncPlans = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPushSelected))]
    [NotifyPropertyChangedFor(nameof(CanPullSelected))]
    [NotifyPropertyChangedFor(nameof(IsConflictSelected))]
    private VaultSyncPlanItem? _selectedPlan;

    [ObservableProperty] private ObservableCollection<string> _availableCampaigns = new();

    [ObservableProperty] private int _aheadCount;

    [ObservableProperty] private int _behindCount;

    [ObservableProperty] private int _conflictCount;

    [ObservableProperty] private string _connectionBanner = string.Empty;

    [ObservableProperty] private bool _showConnectionBanner;

    public SyncViewModel(SettingsViewModel settings)
    {
        _settings = settings;
    }

    public void Bind(CampaignVaultSession? session, Action? refreshExplorer = null)
    {
        _session = session;
        _refreshExplorer = refreshExplorer;
        ClearPlans();
        UpdateSummary();
    }

    public void ClearPlans()
    {
        SyncPlans.Clear();
        SelectedPlan = null;
        if (_session is not { IsOpen: true })
            StatusMessage = "Open a vault to sync with Campaign Vault.";
    }

    internal CampaignSync.CampaignSyncClient CreateClient()
    {
        var port = _settings.GrpcPortValue is > 0 and <= 65535
            ? (int)_settings.GrpcPortValue.Value
            : 50051;
        var token = string.IsNullOrWhiteSpace(_settings.GrpcToken) ? null : _settings.GrpcToken;
        return VaultGrpcClientFactory.CreateClient(_settings.GrpcHost, port, token);
    }

    public void ConfigureSessionSync()
    {
        if (_session is not { IsOpen: true })
            return;

        var settings = new CampaignAuthoringSettings
        {
            GrpcHost = _settings.GrpcHost,
            GrpcPort = _settings.GrpcPortValue is > 0 and <= 65535 ? (int)_settings.GrpcPortValue.Value : 50051,
            GrpcToken = _settings.GrpcToken
        };
        _session.ConfigureVaultSync(CreateClient, settings);
    }

    [RelayCommand]
    internal async Task FetchCampaignsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Fetching campaign list from server...";
        try
        {
            var client = CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await client.GetCampaignsAsync(
                new EmptyRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cts.Token);
            AvailableCampaigns.Clear();
            foreach (var c in response.Campaigns)
                AvailableCampaigns.Add(c.Name);

            StatusMessage = AvailableCampaigns.Count > 0
                ? $"Found {AvailableCampaigns.Count} campaign(s) on Campaign Vault."
                : "No campaigns found on server.";
            SetConnectionBanner(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to fetch campaigns: {ex.Message}";
            SetConnectionBanner(true, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (IsBusy || _session is not { IsOpen: true })
            return;

        IsBusy = true;
        StatusMessage = "Fetching remote snapshot...";
        try
        {
            ConfigureSessionSync();
            await _session.FetchAsync();
            await RefreshPlansAsync();
            LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SetConnectionBanner(false);
        }
        catch (VaultException ex)
        {
            StatusMessage = ex.Message;
            SetConnectionBanner(true, ex.Message);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fetch failed: {ex.Message}";
            SetConnectionBanner(true, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshPlansAsync()
    {
        if (_session is not { IsOpen: true })
            return;

        ConfigureSessionSync();
        var selectedId = SelectedPlan?.Plan.EntityId;
        SyncPlans.Clear();

        foreach (var plan in _session.GetEntitySyncPlans()
                     .Where(p => p.State is not VaultSyncState.Synced and not VaultSyncState.Absent)
                     .OrderBy(p => p.EntityId, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = plan.RelativePath != null
                ? Path.GetFileNameWithoutExtension(plan.RelativePath)
                : plan.EntityId;
            SyncPlans.Add(new VaultSyncPlanItem(plan, displayName));
        }

        SelectedPlan = selectedId != null
            ? SyncPlans.FirstOrDefault(p =>
                string.Equals(p.Plan.EntityId, selectedId, StringComparison.OrdinalIgnoreCase))
            : SyncPlans.FirstOrDefault();

        if (SelectedPlan != null)
            await LoadPlanContentAsync(SelectedPlan);

        UpdateSummary();
        StatusMessage = SyncPlans.Count > 0
            ? $"{SyncPlans.Count} entity(ies) need attention."
            : "Vault is in sync with the remote cache.";
    }

    [RelayCommand]
    private async Task PushAllAsync()
    {
        await ExecuteSyncAsync(() => _session!.PushAsync(), "Push");
    }

    [RelayCommand]
    private async Task PullAllAsync()
    {
        await ExecuteSyncAsync(() => _session!.PullAsync(), "Pull");
    }

    [RelayCommand]
    private async Task PushSelectedAsync()
    {
        if (SelectedPlan == null || _session is not { IsOpen: true })
            return;

        await ExecuteSyncAsync(
            () => _session.PushAsync([SelectedPlan.Plan.EntityId]),
            $"Push {SelectedPlan.DisplayName}");
    }

    [RelayCommand]
    private async Task PullSelectedAsync()
    {
        if (SelectedPlan == null || _session is not { IsOpen: true })
            return;

        await ExecuteSyncAsync(
            () => _session.PullAsync([SelectedPlan.Plan.EntityId]),
            $"Pull {SelectedPlan.DisplayName}");
    }

    [RelayCommand]
    private async Task ResolveKeepLocalAsync()
    {
        if (SelectedPlan == null || _session is not { IsOpen: true })
            return;

        await ExecuteSyncAsync(
            () => _session.ResolveConflictAsync(SelectedPlan.Plan.EntityId, ConflictResolution.KeepLocal),
            $"Keep local for {SelectedPlan.DisplayName}");
    }

    [RelayCommand]
    private async Task ResolveKeepVaultAsync()
    {
        if (SelectedPlan == null || _session is not { IsOpen: true })
            return;

        await ExecuteSyncAsync(
            () => _session.ResolveConflictAsync(SelectedPlan.Plan.EntityId, ConflictResolution.KeepVault),
            $"Keep vault for {SelectedPlan.DisplayName}");
    }

    [RelayCommand]
    private async Task ResolveMergedAsync()
    {
        if (SelectedPlan == null || _session is not { IsOpen: true })
            return;

        var mergedContent = SelectedPlan.MergedContent;
        await ExecuteSyncAsync(
            () => _session.ResolveConflictAsync(SelectedPlan.Plan.EntityId, ConflictResolution.Merged, mergedContent),
            $"Save merged content for {SelectedPlan.DisplayName}");
    }

    private async Task ExecuteSyncAsync(Func<Task> action, string label)
    {
        if (IsBusy || _session is not { IsOpen: true })
            return;

        IsBusy = true;
        StatusMessage = $"{label} in progress...";
        try
        {
            ConfigureSessionSync();
            await action();
            await RefreshPlansAsync();
            _refreshExplorer?.Invoke();
            LastSyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            StatusMessage = $"{label} completed.";
            SetConnectionBanner(false);
        }
        catch (VaultException ex)
        {
            StatusMessage = ex.Message;
            SetConnectionBanner(true, ex.Message);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label} failed: {ex.Message}";
            SetConnectionBanner(true, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool CanPushSelected => !IsBusy && SelectedPlan != null &&
                                   SelectedPlan.Plan.State is VaultSyncState.AheadOfVault
                                       or VaultSyncState.LocalOnly
                                       or VaultSyncState.DeletedLocally;

    public bool CanPullSelected => !IsBusy && SelectedPlan != null &&
                                   SelectedPlan.Plan.State is VaultSyncState.BehindVault
                                       or VaultSyncState.RemoteOnly
                                       or VaultSyncState.DeletedRemotely;

    public bool IsConflictSelected => SelectedPlan?.Plan.State == VaultSyncState.Conflict;

    partial void OnSelectedPlanChanged(VaultSyncPlanItem? value)
    {
        OnPropertyChanged(nameof(CanPushSelected));
        OnPropertyChanged(nameof(CanPullSelected));
        OnPropertyChanged(nameof(IsConflictSelected));

        if (value != null)
            _ = LoadPlanContentAsync(value);
    }

    private async Task LoadPlanContentAsync(VaultSyncPlanItem item)
    {
        if (_session is not { IsOpen: true })
            return;

        var plan = item.Plan;
        item.LocalContent = string.Empty;
        item.RemoteContent = string.Empty;

        if (!string.IsNullOrWhiteSpace(plan.RelativePath))
        {
            try
            {
                item.LocalContent = await _session.ReadFileAsync(plan.RelativePath);
            }
            catch
            {
                item.LocalContent = "(no local file)";
            }
        }
        else
        {
            item.LocalContent = "(no local file)";
        }

        var cachePath = Path.Combine(
            _session.VaultPath!,
            VaultPaths.AppConfigDirectoryName,
            "remote-cache",
            "entities",
            plan.EntityId.Replace('\\', '/') + ".md");

        if (File.Exists(cachePath))
            item.RemoteContent = await File.ReadAllTextAsync(cachePath);
        else
            item.RemoteContent = "(not in remote cache — fetch first)";

        item.MergedContent = plan.State == VaultSyncState.Conflict ? item.LocalContent : string.Empty;
    }

    public void UpdateSummary()
    {
        if (_session is not { IsOpen: true })
        {
            AheadCount = BehindCount = ConflictCount = 0;
            return;
        }

        ConfigureSessionSync();
        var summary = _session.GetSyncSummary();
        AheadCount = summary.AheadCount;
        BehindCount = summary.BehindCount;
        ConflictCount = summary.ConflictCount;

        var connection = summary.Connection;
        if (connection.State is VaultConnectionState.Offline or VaultConnectionState.Error)
            SetConnectionBanner(true, connection.Message ?? connection.State.ToString());
        else if (summary.RemoteCacheCorrupt)
            SetConnectionBanner(true, "Remote cache manifest is corrupt. Fetch again.");
        else
            SetConnectionBanner(false);
    }

    private void SetConnectionBanner(bool show, string? message = null)
    {
        ShowConnectionBanner = show;
        ConnectionBanner = message ?? string.Empty;
    }
}