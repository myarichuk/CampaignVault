using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class HubViewModel : ViewModelBase
{
    private readonly CampaignHistoryService _historyService = new();
    private readonly MainWindowViewModel _mainViewModel;

    [ObservableProperty] private ObservableCollection<string> _recentCampaigns = new();

    [ObservableProperty] private ObservableCollection<string> _remoteCampaigns = new();

    [ObservableProperty] private string _statusMessage = "Welcome to CampaignVault Authoring — entities only, no simulation sync.";

    [ObservableProperty] private bool _isCloudConnected;

    [ObservableProperty] private bool _isBusy;

    public HubViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        LoadRecentCampaigns();
    }

    [RelayCommand]
    public async Task RefreshCloudAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Connecting to Campaign Vault...";
        try
        {
            await _mainViewModel.Sync.FetchCampaignsAsync();
            RemoteCampaigns.Clear();
            foreach (var campaign in _mainViewModel.Sync.AvailableCampaigns)
                RemoteCampaigns.Add(campaign);

            IsCloudConnected = RemoteCampaigns.Count > 0;
            StatusMessage = IsCloudConnected
                ? $"Connected: {RemoteCampaigns.Count} campaign(s) on Campaign Vault."
                : "Connected, but no campaigns found on server.";
        }
        catch (Exception ex)
        {
            IsCloudConnected = false;
            StatusMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void LoadRecentCampaigns()
    {
        RecentCampaigns.Clear();
        foreach (var path in _historyService.Load().RecentPaths)
            RecentCampaigns.Add(path);
    }

    [RelayCommand]
    private async Task OpenCampaign(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Directory.Exists(path))
            {
                _historyService.Add(path);
                await _mainViewModel.LoadCampaignAsync(path);
            }
            else
            {
                StatusMessage = $"Directory not found: {path}";
                _historyService.Remove(path);
                LoadRecentCampaigns();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveFromRecent(string path)
    {
        _historyService.Remove(path);
        LoadRecentCampaigns();
        StatusMessage = "Removed from recent vaults.";
    }

    [RelayCommand]
    private async Task CreateNewCampaign()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Creating new vault...";
        try
        {
            var path = await _mainViewModel.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "Create cancelled: no folder selected.";
                return;
            }

            var campaignName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            await _mainViewModel.Session.CreateAsync(path, campaignName);
            _historyService.Add(path);
            LoadRecentCampaigns();

            _mainViewModel.Sync.ConfigureSessionSync();
            _mainViewModel.Workspace.BindSession(_mainViewModel.Session);
            _mainViewModel.Sync.Bind(_mainViewModel.Session, _mainViewModel.RefreshAll);
            _mainViewModel.SourceControl.Bind(_mainViewModel.Session, _mainViewModel.RefreshAll);
            WorkspaceService.VaultSession = _mainViewModel.Session;

            _mainViewModel.ApplicationState.CurrentState = AppState.Editor;
            _mainViewModel.WorkspaceStatusMessage = $"Vault: {path}";
            _mainViewModel.RefreshAll();
            StatusMessage = $"Created vault '{campaignName}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Create failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadRemoteCampaign(string campaignName)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = $"Importing '{campaignName}' from Campaign Vault...";

        try
        {
            var path = await _mainViewModel.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "Import cancelled: no folder selected.";
                return;
            }

            await _mainViewModel.Session.CreateAsync(path, campaignName);
            _historyService.Add(path);

            _mainViewModel.Sync.ConfigureSessionSync();
            _mainViewModel.Workspace.BindSession(_mainViewModel.Session);
            _mainViewModel.Sync.Bind(_mainViewModel.Session, _mainViewModel.RefreshAll);
            _mainViewModel.SourceControl.Bind(_mainViewModel.Session, _mainViewModel.RefreshAll);
            WorkspaceService.VaultSession = _mainViewModel.Session;
            _mainViewModel.ApplicationState.CurrentState = AppState.Editor;

            StatusMessage = $"Fetching '{campaignName}'...";
            await _mainViewModel.Sync.FetchCommand.ExecuteAsync(null);
            await _mainViewModel.Sync.PullAllCommand.ExecuteAsync(null);

            _mainViewModel.RefreshAll();
            StatusMessage = $"Imported '{campaignName}' into local vault.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}