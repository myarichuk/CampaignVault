using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampaignVault.Authoring.Services;

namespace CampaignVault.Authoring.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceDbService _dbService = new();
    private readonly WorkspaceParser _parser = new();
    private WorkspaceScanner? _scanner;

    private IStorageProvider? _storageProvider;
    private CancellationTokenSource? _debounceSource;

    [ObservableProperty]
    private ObservableCollection<FileNodeViewModel> _files = new();

    [ObservableProperty]
    private FileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    [ObservableProperty]
    private string _workspaceStatusMessage = "Open a campaign folder to begin.";

    private FileSystemWatcher? _watcher;

    public WorkspaceDbService DbService => _dbService;

    public void SetStorageProvider(IStorageProvider sp) { _storageProvider = sp; }

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

                var selectedPath = SelectedFile?.FilePath;

                if (_scanner != null && !string.IsNullOrEmpty(CurrentDirectory))
                {
                    await _scanner.ScanWorkspaceAsync(CurrentDirectory);
                }

                RefreshFilesList();

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    var found = Files.FirstOrDefault(f => f.FilePath == selectedPath);
                    if (found != null)
                    {
                        SelectedFile = found;
                        WorkspaceService.MainWindowViewModel?.ReloadActiveFileContent();
                    }
                    else
                    {
                        SelectedFile = null;
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
        Files.Clear();
        if (!Directory.Exists(CurrentDirectory)) return;

        try
        {
            var entities = _dbService.GetAllEntities();
            foreach (var entity in entities)
            {
                var absolutePath = Path.Combine(CurrentDirectory, entity.RelativePath);
                Files.Add(new FileNodeViewModel
                {
                    FileName = Path.GetFileName(absolutePath),
                    FilePath = absolutePath,
                    EntityType = entity.EntityType,
                    EntityId = entity.Id,
                    SyncStatus = entity.SyncStatus
                });
            }
        }
        catch {}
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
    }
}

public partial class FileNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _entityType = string.Empty;

    [ObservableProperty]
    private string _entityId = string.Empty;

    [ObservableProperty]
    private string _syncStatus = "Synced";
}

