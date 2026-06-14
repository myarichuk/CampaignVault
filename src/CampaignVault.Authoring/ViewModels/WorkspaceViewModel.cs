using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Models;

namespace CampaignVault.Authoring.ViewModels;

public partial class ExplorerNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;
    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = new();
}

public partial class CategoryNodeViewModel : ExplorerNodeViewModel
{
}

public partial class EntityNodeViewModel : ExplorerNodeViewModel
{
    public UnifiedEntity Entity { get; }

    public EntityNodeViewModel(UnifiedEntity entity)
    {
        Entity = entity;
        Title = entity.Name;
    }
}

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceDbService _dbService = new();
    private readonly WorkspaceParser _parser = new();
    private WorkspaceScanner? _scanner;

    private IStorageProvider? _storageProvider;
    private CancellationTokenSource? _debounceSource;
    private CampaignStateService? _stateService;

    [ObservableProperty]
    private ObservableCollection<ExplorerNodeViewModel> _categories = new();

    [ObservableProperty]
    private ExplorerNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    [ObservableProperty]
    private string _workspaceStatusMessage = "Open a campaign folder to begin.";

    private FileSystemWatcher? _watcher;

    public WorkspaceDbService DbService => _dbService;
    public CampaignStateService? StateService => _stateService;

    public void SetStorageProvider(IStorageProvider sp) { _storageProvider = sp; }

    public async Task RefreshStateAsync()
    {
        if (_stateService != null && !string.IsNullOrEmpty(CurrentDirectory))
        {
            await _stateService.RefreshStateAsync(Path.GetFileName(CurrentDirectory));
        }
    }

    public async Task RefreshLocalStateAsync()
    {
        if (_stateService != null)
        {
            await _stateService.RefreshLocalStateOnlyAsync();
        }
    }

    public void SetStateService(CampaignStateService stateService)
    {
        if (_stateService != null)
        {
            _stateService.StateChanged -= OnStateChanged;
        }
        _stateService = stateService;
        _stateService.StateChanged += OnStateChanged;
        RefreshFilesList();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshFilesList);
    }

    [RelayCommand]
    private async Task OpenCampaignFolderAsync()
    {
        if (_storageProvider == null)
        {
            WorkspaceStatusMessage = "Folder picker is not available yet.";
            return;
        }

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Campaign Workspace Folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder == null) return;

        var path = folder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            WorkspaceStatusMessage = "Could not resolve the selected folder path.";
            return;
        }

        LoadDirectory(path);
        WorkspaceStatusMessage = $"Workspace: {path}";
    }

    public void LoadDirectory(string path)
    {
        CurrentDirectory = path;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        if (Directory.Exists(path))
        {
            _dbService.InitializeDatabase(path);
            _scanner = new WorkspaceScanner(_dbService, _parser);
            
            // Sync-scan on load
            Task.Run(async () =>
            {
                await _scanner.ScanWorkspaceAsync(path);
                if (_stateService != null)
                {
                    await _stateService.RefreshStateAsync(Path.GetFileName(path));
                }
                Avalonia.Threading.Dispatcher.UIThread.Post(RefreshFilesList);
            });

            _watcher = new FileSystemWatcher(path, "*.md")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnWorkspaceChanged;
            _watcher.Deleted += OnWorkspaceChanged;
            _watcher.Changed += OnWorkspaceChanged;
            _watcher.Renamed += OnWorkspaceChanged;
        }
        else
        {
            RefreshFilesList();
        }
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        // Cancel any pending debounced scan and start a new one
        _debounceSource?.Cancel();
        _debounceSource = new CancellationTokenSource();
        var token = _debounceSource.Token;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(400, token);

                var selectedEntityId = (SelectedNode as EntityNodeViewModel)?.Entity.Id;

                if (_scanner != null && !string.IsNullOrEmpty(CurrentDirectory))
                {
                    await _scanner.ScanWorkspaceAsync(CurrentDirectory);
                    if (_stateService != null)
                    {
                        await _stateService.RefreshStateAsync(Path.GetFileName(CurrentDirectory));
                    }
                }

                RefreshFilesList();

                if (!string.IsNullOrEmpty(selectedEntityId))
                {
                    foreach (var cat in Categories)
                    {
                        var found = cat.Children.OfType<EntityNodeViewModel>().FirstOrDefault(f => f.Entity.Id == selectedEntityId);
                        if (found != null)
                        {
                            SelectedNode = found;
                            WorkspaceService.MainWindowViewModel?.ReloadActiveFileContent();
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled — a newer event will take over
            }
        });
    }

    public void RefreshFilesList()
    {
        Categories.Clear();
        if (_stateService == null) return;

        var groups = _stateService.Entities
            .GroupBy(e => e.EntityType)
            .OrderBy(g => g.Key);

        foreach (var group in groups)
        {
            var category = new CategoryNodeViewModel { Title = group.Key + "s" };
            foreach (var entity in group.OrderBy(e => e.Name))
            {
                category.Children.Add(new EntityNodeViewModel(entity));
            }
            Categories.Add(category);
        }
    }

    public void Dispose()
    {
        _debounceSource?.Cancel();
        _debounceSource?.Dispose();
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_stateService != null)
        {
            _stateService.StateChanged -= OnStateChanged;
        }
    }
}

