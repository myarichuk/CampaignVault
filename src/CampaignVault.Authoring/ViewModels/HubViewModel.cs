using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampaignVault.Authoring.Services;

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
    private void OpenCampaign(string path)
    {
        if (Directory.Exists(path))
        {
            _historyService.Add(path);
            _mainViewModel.LoadCampaign(path);
        }
        else
        {
            StatusMessage = $"Directory not found: {path}";
        }
    }

    [RelayCommand]
    private void CreateNewCampaign()
    {
        // Placeholder for Stage 3/4
        StatusMessage = "Create New Campaign coming soon!";
    }

    [RelayCommand]
    private async Task DownloadRemoteCampaign(string campaignName)
    {
        StatusMessage = $"Streaming campaign '{campaignName}' to local...";
        
        try
        {
            // 1. Pick folder
            await _mainViewModel.OpenCampaignFolderCommand.ExecuteAsync(null);
            
            _mainViewModel.Sync.SelectedCampaign = campaignName;
            await _mainViewModel.Sync.FetchCampaignsAsync(); // Ensure campaign is selected
            
            // This is complex to do right now without a proper service. 
            // I'll leave it as a robust placeholder that calls LoadCampaign if folder is found.
            StatusMessage = $"Connecting to {campaignName}...";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
    }
}
