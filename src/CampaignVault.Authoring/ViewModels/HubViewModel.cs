using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Authoring.ViewModels;

public partial class HubViewModel : ViewModelBase
{
    private readonly CampaignHistoryService _historyService = new();
    private readonly MainWindowViewModel _mainViewModel;

    [ObservableProperty]
    private ObservableCollection<string> _recentCampaigns = new();

    [ObservableProperty]
    private ObservableCollection<string> _remoteCampaigns = new();

    [ObservableProperty]
    private string _statusMessage = "Welcome to CampaignVault Authoring";

    [ObservableProperty]
    private bool _isCloudConnected;

    public HubViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        LoadRecentCampaigns();
    }

    [RelayCommand]
    public async Task RefreshCloudAsync()
    {
        StatusMessage = "Connecting to Vault...";
        try
        {
            await _mainViewModel.Sync.FetchCampaignsAsync();
            RemoteCampaigns.Clear();
            foreach (var campaign in _mainViewModel.Sync.AvailableCampaigns)
            {
                RemoteCampaigns.Add(campaign);
            }
            IsCloudConnected = true;
            StatusMessage = $"Cloud Connected: Found {RemoteCampaigns.Count} campaigns.";
        }
        catch (Exception ex)
        {
            IsCloudConnected = false;
            StatusMessage = $"Cloud Error: {ex.Message}";
        }
    }

    public void LoadRecentCampaigns()
    {
        var history = _historyService.Load();
        RecentCampaigns.Clear();
        foreach (var path in history.RecentPaths)
        {
            RecentCampaigns.Add(path);
        }
    }

    [RelayCommand]
    private async Task OpenCampaign(string path)
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

    [RelayCommand]
    private void RemoveFromRecent(string path)
    {
        _historyService.Remove(path);
        LoadRecentCampaigns();
        StatusMessage = "Removed from recent campaigns.";
    }

    [RelayCommand]
    private async Task CreateNewCampaign()
    {
        StatusMessage = "Creating new campaign...";
        try
        {
            var path = await _mainViewModel.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "Create cancelled: No folder selected.";
                return;
            }

            var campaignName = Path.GetFileName(path);
            var metadataService = new MetadataService();
            await metadataService.SaveMetadataAsync(path, new VaultMetadata 
            { 
                CampaignName = campaignName 
            });

            await _mainViewModel.LoadCampaignAsync(path);
            StatusMessage = $"Created new campaign '{campaignName}' locally.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Create failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadRemoteCampaign(string campaignName)
    {
        StatusMessage = $"Preparing to stream campaign '{campaignName}'...";
        
        try
        {
            // 1. Pick folder
            var path = await _mainViewModel.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                StatusMessage = "Download cancelled: No folder selected.";
                return;
            }

            // 2. Create vault-metadata.json
            var metadataService = new MetadataService();
            await metadataService.SaveMetadataAsync(path, new VaultMetadata 
            { 
                CampaignName = campaignName 
            });

            // 3. Load the campaign into workspace
            _mainViewModel.Sync.SelectedCampaign = campaignName;
            await _mainViewModel.LoadCampaignAsync(path);

            // 4. Trigger the initial sync (Pull everything)
            StatusMessage = $"Streaming '{campaignName}' contents...";
            await _mainViewModel.Sync.PopulateActualDiffsAsync();
            
            if (_mainViewModel.Sync.SyncDiffs.Any())
            {
                await _mainViewModel.Sync.SyncAllAsync();
                StatusMessage = $"Successfully downloaded '{campaignName}'.";
            }
            else
            {
                StatusMessage = $"Campaign '{campaignName}' is empty or already up to date.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
    }
}
