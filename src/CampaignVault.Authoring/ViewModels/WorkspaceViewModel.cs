using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CampaignVault.Authoring.Services;

namespace CampaignVault.Authoring.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceDbService _dbService = new();
    private readonly WorkspaceParser _parser = new();
    private WorkspaceScanner? _scanner;

    [ObservableProperty]
    private ObservableCollection<FileNodeViewModel> _files = new();

    [ObservableProperty]
    private FileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private string _currentDirectory = string.Empty;

    private FileSystemWatcher? _watcher;

    public WorkspaceDbService DbService => _dbService;

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
        // Must run on Avalonia UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
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

