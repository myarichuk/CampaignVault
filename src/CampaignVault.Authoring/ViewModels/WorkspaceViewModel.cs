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

public partial class FolderNodeViewModel : ExplorerNodeViewModel
{
    public string FolderPath { get; init; } = string.Empty;
}

public partial class EntityNodeViewModel : ExplorerNodeViewModel
{
    public UnifiedEntity Entity { get; private set; }

    public EntityNodeViewModel(UnifiedEntity entity)
    {
        Entity = entity;
        Title = entity.Name;
    }

    public void SyncFrom(UnifiedEntity entity)
    {
        Entity = entity;
        Title = entity.Name;
    }
}

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    public static Avalonia.Data.Converters.FuncValueConverter<object?, double> NullToOpacityConverter { get; } =
        new(v => v == null ? 0.6 : 1.0);

    private readonly WorkspaceDbService _dbService = new();
    public WorkspaceParser Parser { get; } = new();
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

    [RelayCommand]
    public async Task RefreshStateAsync()
    {
        if (_stateService != null)
        {
            var campaignName = Path.GetFileName(CurrentDirectory);
            try 
            {
                var metadata = await new MetadataService().LoadMetadataAsync(CurrentDirectory);
                if (metadata != null && !string.IsNullOrEmpty(metadata.CampaignName))
                    campaignName = metadata.CampaignName;
            } catch {}

            await _stateService.RefreshStateAsync(campaignName);
        }
    }

    public void LoadDirectory(string path)
    {
        _ = LoadDirectoryAsync(path);
    }

    public async Task LoadDirectoryAsync(string path)
    {
        CurrentDirectory = path;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        if (!Directory.Exists(path))
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshFilesList);
            return;
        }

        _dbService.InitializeDatabase(path);
        _scanner = new WorkspaceScanner(_dbService, Parser);

        await _scanner.ScanWorkspaceAsync(path);

        var campaignName = Path.GetFileName(path);
        try
        {
            var metadata = await new MetadataService().LoadMetadataAsync(path);
            if (metadata != null && !string.IsNullOrEmpty(metadata.CampaignName))
                campaignName = metadata.CampaignName;
        }
        catch { }

        if (_stateService != null)
            await _stateService.RefreshStateAsync(campaignName);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshFilesList);

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
                        var campaignName = Path.GetFileName(CurrentDirectory);
                        try 
                        {
                            var metadata = await new MetadataService().LoadMetadataAsync(CurrentDirectory);
                            if (metadata != null && !string.IsNullOrEmpty(metadata.CampaignName))
                                campaignName = metadata.CampaignName;
                        } catch {}
                        
                        await _stateService.RefreshStateAsync(campaignName);
                    }
                }

                RefreshFilesList();

                if (!string.IsNullOrEmpty(selectedEntityId))
                {
                    var found = FindEntityNode(Categories, selectedEntityId);
                    if (found != null)
                    {
                        SelectedNode = found;
                        WorkspaceService.MainWindowViewModel?.ReloadActiveFileContent();
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
        if (_stateService == null) return;

        var entities = _stateService.Entities.ToList();
        var categoryKeys = entities
            .Select(GetCategoryKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toRemoveCats = Categories
            .Where(c => !categoryKeys.Contains(c.Title, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var c in toRemoveCats) Categories.Remove(c);

        foreach (var categoryKey in categoryKeys)
        {
            var categoryEntities = entities
                .Where(e => string.Equals(GetCategoryKey(e), categoryKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.RelativePath ?? e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var category = Categories.FirstOrDefault(c =>
                string.Equals(c.Title, categoryKey, StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                category = new CategoryNodeViewModel { Title = categoryKey };
                Categories.Add(category);
            }

            SyncFolderChildren(category, categoryKey, categoryEntities, categoryKey);
        }
    }

    private static string GetCategoryKey(UnifiedEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.RelativePath))
        {
            var parts = entity.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts[0];
        }

        return entity.EntityType + "s";
    }

    private static void SyncFolderChildren(
        ExplorerNodeViewModel parent,
        string parentPath,
        IReadOnlyList<UnifiedEntity> entities,
        string categoryKey)
    {
        var childFolders = entities
            .Select(e => GetChildFolderName(e, parentPath, categoryKey))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var directEntities = entities
            .Where(e => GetChildFolderName(e, parentPath, categoryKey) == null)
            .ToList();

        var expectedChildIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in childFolders)
            expectedChildIds.Add($"folder:{parentPath}/{folderName}");
        foreach (var entity in directEntities)
            expectedChildIds.Add($"entity:{entity.Id}");

        var staleChildren = parent.Children
            .Where(child => !IsExpectedChild(child, parentPath, categoryKey, expectedChildIds))
            .ToList();
        foreach (var stale in staleChildren)
            parent.Children.Remove(stale);

        foreach (var folderName in childFolders)
        {
            var folderPath = $"{parentPath}/{folderName}";
            var folder = parent.Children.OfType<FolderNodeViewModel>()
                .FirstOrDefault(f => string.Equals(f.FolderPath, folderPath, StringComparison.OrdinalIgnoreCase));
            if (folder == null)
            {
                folder = new FolderNodeViewModel
                {
                    Title = folderName!,
                    FolderPath = folderPath
                };
                InsertFolderNode(parent, folder);
            }

            var folderEntities = entities
                .Where(e => string.Equals(GetChildFolderName(e, parentPath, categoryKey), folderName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncFolderChildren(folder, folderPath, folderEntities, categoryKey);
        }

        foreach (var entity in directEntities.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var existing = parent.Children.OfType<EntityNodeViewModel>()
                .FirstOrDefault(n => string.Equals(n.Entity.Id, entity.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                InsertEntityNode(parent, new EntityNodeViewModel(entity));
            }
            else
            {
                existing.SyncFrom(entity);
            }
        }
    }

    private static string? GetChildFolderName(UnifiedEntity entity, string parentPath, string categoryKey)
    {
        if (string.IsNullOrWhiteSpace(entity.RelativePath))
            return null;

        var parts = entity.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var relativeParts = parts.SkipWhile(p =>
            !string.Equals(p, categoryKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (relativeParts.Length == 0)
            relativeParts = parts;

        var parentParts = parentPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (relativeParts.Length <= parentParts.Length + 1)
            return null;

        return relativeParts[parentParts.Length];
    }

    private static bool IsExpectedChild(
        ExplorerNodeViewModel child,
        string parentPath,
        string categoryKey,
        HashSet<string> expectedChildIds)
    {
        return child switch
        {
            FolderNodeViewModel folder => expectedChildIds.Contains($"folder:{folder.FolderPath}"),
            EntityNodeViewModel entity => expectedChildIds.Contains($"entity:{entity.Entity.Id}"),
            _ => false
        };
    }

    private static void InsertFolderNode(ExplorerNodeViewModel parent, FolderNodeViewModel folder)
    {
        var index = GetSortedInsertIndex(parent, folder.Title, node => node is FolderNodeViewModel);
        parent.Children.Insert(index, folder);
    }

    private static void InsertEntityNode(ExplorerNodeViewModel parent, EntityNodeViewModel entityNode)
    {
        var index = GetSortedInsertIndex(parent, entityNode.Title, node => node is EntityNodeViewModel);
        parent.Children.Insert(index, entityNode);
    }

    private static int GetSortedInsertIndex(
        ExplorerNodeViewModel parent,
        string title,
        Func<ExplorerNodeViewModel, bool> matchesType)
    {
        var index = 0;
        foreach (var child in parent.Children)
        {
            if (!matchesType(child))
            {
                index++;
                continue;
            }

            if (string.Compare(child.Title, title, StringComparison.OrdinalIgnoreCase) > 0)
                break;
            index++;
        }

        return index;
    }

    private static EntityNodeViewModel? FindEntityNode(
        IEnumerable<ExplorerNodeViewModel> nodes,
        string entityId)
    {
        foreach (var node in nodes)
        {
            if (node is EntityNodeViewModel entityNode &&
                string.Equals(entityNode.Entity.Id, entityId, StringComparison.OrdinalIgnoreCase))
            {
                return entityNode;
            }

            var nested = FindEntityNode(node.Children, entityId);
            if (nested != null)
                return nested;
        }

        return null;
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

