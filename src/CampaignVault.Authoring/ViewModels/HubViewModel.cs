using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class HubViewModel : ViewModelBase
{
    private readonly CampaignHistoryService _historyService = new();
    private readonly MainWindowViewModel _mainViewModel;

    public MainWindowViewModel MainViewModel => _mainViewModel;

    [ObservableProperty] private ObservableCollection<CampaignListItem> _recentCampaigns = new();

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

            IsCloudConnected = true;
            StatusMessage = RemoteCampaigns.Count > 0
                ? $"Connected: {RemoteCampaigns.Count} campaign(s) on Campaign Vault."
                : "Connected to Campaign Vault, but no campaigns exist yet. Create one in play MCP first.";
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

        var campaignsRootPath = _mainViewModel.Settings.CampaignsRootPath;

        if (!string.IsNullOrWhiteSpace(campaignsRootPath) && Directory.Exists(campaignsRootPath))
        {
            try
            {
                var subdirs = Directory.GetDirectories(campaignsRootPath)
                    .OrderByDescending(d => new DirectoryInfo(d).LastAccessTime);

                foreach (var dir in subdirs)
                {
                    RecentCampaigns.Add(new CampaignListItem { Path = dir, Exists = true });
                }

                if (subdirs.Any())
                    StatusMessage = $"Loaded {subdirs.Count()} campaign(s) from folder.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error scanning campaigns folder: {ex.Message}";
            }
        }
        else
        {
            foreach (var path in _historyService.Load().RecentPaths)
            {
                var exists = Directory.Exists(path);
                RecentCampaigns.Add(new CampaignListItem { Path = path, Exists = exists });
            }
        }
    }

    [RelayCommand]
    private async Task OpenCampaign(CampaignListItem item)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Directory.Exists(item.Path))
            {
                _historyService.Add(item.Path);
                await _mainViewModel.LoadCampaignAsync(item.Path);
            }
            else
            {
                StatusMessage = $"Directory not found: {item.Path}";
                _historyService.Remove(item.Path);
                LoadRecentCampaigns();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveFromRecent(CampaignListItem item)
    {
        _historyService.Remove(item.Path);
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

            var folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var campaignName = CampaignSlug.Canonicalize(folderName);
            await _mainViewModel.Session.CreateAsync(path, campaignName);
            _historyService.Add(path);
            LoadRecentCampaigns();

            _mainViewModel.EnterEditorMode(path);
            _mainViewModel.RefreshAll();
            StatusMessage = folderName != campaignName
                ? $"Created vault '{campaignName}' (from folder '{folderName}'). Slug must match the server campaign for gRPC sync."
                : $"Created vault '{campaignName}'.";
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
            LoadRecentCampaigns();
            _mainViewModel.EnterEditorMode(path);

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

    [RelayCommand]
    private async Task SetCampaignsFolderAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Selecting campaigns folder...";

        try
        {
            var path = await _mainViewModel.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "Cancelled: no folder selected.";
                return;
            }

            _mainViewModel.Settings.CampaignsRootPath = path;
            _mainViewModel.Settings.SaveCommand.Execute(null);
            LoadRecentCampaigns();
            StatusMessage = $"Campaigns folder set to: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error setting campaigns folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearCampaignsFolder()
    {
        _mainViewModel.Settings.CampaignsRootPath = string.Empty;
        _mainViewModel.Settings.SaveCommand.Execute(null);
        LoadRecentCampaigns();
        StatusMessage = "Campaigns folder cleared. Using recent campaign history.";
    }
}

public class CampaignListItem
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
}