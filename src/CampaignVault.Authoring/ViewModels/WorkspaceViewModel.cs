using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Authoring.Vault.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class ExplorerNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = new();
}

public partial class CategoryNodeViewModel : ExplorerNodeViewModel
{
    public string? EntityType { get; init; }

    public string CreateEntityMenuLabel => EntityType is not null
        ? $"New {char.ToUpper(EntityType[0])}{EntityType[1..]}"
        : "New Entity...";
}

public partial class FolderNodeViewModel : ExplorerNodeViewModel
{
    public string FolderPath { get; init; } = string.Empty;
}

public partial class EntityNodeViewModel : ExplorerNodeViewModel
{
    public VaultEntity Entity { get; private set; } = null!;

    [ObservableProperty] private VaultSyncState _syncState;

    [ObservableProperty] private bool _isGitDirty;

    [ObservableProperty] private bool _hasLocalFile;

    public EntityNodeViewModel(VaultEntity entity, VaultSyncState syncState, bool isGitDirty, bool hasLocalFile,
        string? vaultPath = null)
    {
        SyncFrom(entity, syncState, isGitDirty, hasLocalFile, vaultPath);
    }

    public void SyncFrom(VaultEntity entity, VaultSyncState syncState, bool isGitDirty, bool hasLocalFile,
        string? vaultPath = null)
    {
        Entity = entity;
        SyncState = syncState;
        IsGitDirty = isGitDirty;
        HasLocalFile = hasLocalFile;
        Title = VaultEntityDisplay.GetDisplayName(entity, vaultPath);
    }
}

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    public static Avalonia.Data.Converters.FuncValueConverter<object?, double> NullToOpacityConverter { get; } =
        new(v => v == null ? 0.6 : 1.0);

    private CampaignVaultSession? _session;
    private CancellationTokenSource? _debounceSource;
    private FileSystemWatcher? _watcher;

    [ObservableProperty] private ObservableCollection<ExplorerNodeViewModel> _categories = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntity))]
    private ExplorerNodeViewModel? _selectedNode;

    public bool HasSelectedEntity => SelectedNode is EntityNodeViewModel;

    [ObservableProperty] private string _currentDirectory = string.Empty;

    [ObservableProperty] private string _workspaceStatusMessage = "Open a campaign vault to begin.";

    [ObservableProperty] private bool _isLoading;

    public WorkspaceParser Parser { get; } = new();

    public void BindSession(CampaignVaultSession? session)
    {
        StopWatcher();
        _session = session;
        CurrentDirectory = session?.VaultPath ?? string.Empty;
        WorkspaceStatusMessage = session?.IsOpen == true
            ? $"Vault: {session.VaultPath}. Use + or File > New to add entities."
            : "Open a campaign vault to begin.";

        if (session?.IsOpen == true)
            StartWatcher(session.VaultPath!);

        RefreshFilesList();
    }

    [RelayCommand]
    private void RefreshExplorer()
    {
        RefreshFilesList();
    }

    public Func<Task<CreateEntityRequest?>>? RequestEntityCreationAsync { get; set; }

    [RelayCommand]
    private async Task CreateNewEntityAsync(string? entityType = null)
    {
        CreateEntityRequest? request;

        if (string.IsNullOrEmpty(entityType))
        {
            if (RequestEntityCreationAsync == null) return;
            request = await RequestEntityCreationAsync();
        }
        else
        {
            var typeName = char.ToUpper(entityType[0]) + entityType[1..];
            request = new CreateEntityRequest(entityType, $"New {typeName}");
        }

        if (request == null) return;

        var main = App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState;
        if (main != null)
        {
            await main.CreateNewEntityCommand.ExecuteAsync(request);
        }
    }

    [RelayCommand]
    private async Task CreateNewEntityInFolderAsync(string? folderPath = null)
    {
        if (string.IsNullOrEmpty(folderPath)) return;

        var parts = folderPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var entityType = parts[0];
        var targetSubfolder = parts.Length > 1 ? string.Join("/", parts.Skip(1)) : null;

        var typeName = char.ToUpper(entityType[0]) + entityType[1..];
        var request = new CreateEntityRequest(entityType, $"New {typeName}", targetSubfolder);

        var main = App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState;
        if (main != null)
        {
            await main.CreateNewEntityCommand.ExecuteAsync(request);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedEntityAsync()
    {
        var main = App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState;
        if (main != null)
        {
            await main.DeleteSelectedEntityCommand.ExecuteAsync(null);
        }
    }

    public void RefreshFilesList()
    {
        if (_session is not { IsOpen: true })
        {
            Categories.Clear();
            return;
        }

        var entities = _session.ScanEntities();
        var syncPlans = _session.GetEntitySyncPlans()
            .ToDictionary(p => p.EntityId, StringComparer.OrdinalIgnoreCase);
        var gitStatus = _session.GetGitStatus();
        var dirtyPaths = new HashSet<string>(
            gitStatus.ModifiedPaths
                .Concat(gitStatus.AddedPaths)
                .Concat(gitStatus.UntrackedPaths)
                .Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);

        var categoryKeys = entities
            .Select(GetCategoryKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toRemoveCats = Categories
            .Where(c => !categoryKeys.Contains(c.Title, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var c in toRemoveCats)
            Categories.Remove(c);

        foreach (var categoryKey in categoryKeys)
        {
            var categoryEntities = entities
                .Where(e => string.Equals(GetCategoryKey(e), categoryKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var category = Categories.FirstOrDefault(c =>
                string.Equals(c.Title, categoryKey, StringComparison.OrdinalIgnoreCase));
            if (category == null)
            {
                var entityType = VaultPaths.EntityFolders
                    .FirstOrDefault(f => string.Equals(f.Folder, categoryKey, StringComparison.OrdinalIgnoreCase))
                    .EntityType;
                category = new CategoryNodeViewModel { Title = categoryKey, EntityType = entityType };
                Categories.Add(category);
            }

            SyncFolderChildren(category, categoryKey, categoryEntities, categoryKey, syncPlans, dirtyPaths,
                _session?.VaultPath);
        }
    }

    private void StartWatcher(string vaultPath)
    {
        if (!Directory.Exists(vaultPath))
            return;

        _watcher = new FileSystemWatcher(vaultPath, "*.md")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnWorkspaceChanged;
        _watcher.Deleted += OnWorkspaceChanged;
        _watcher.Changed += OnWorkspaceChanged;
        _watcher.Renamed += OnWorkspaceChanged;
    }

    private void StopWatcher()
    {
        _debounceSource?.Cancel();
        _debounceSource?.Dispose();
        _debounceSource = null;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnWorkspaceChanged(object sender, FileSystemEventArgs e)
    {
        _debounceSource?.Cancel();
        _debounceSource = new CancellationTokenSource();
        var token = _debounceSource.Token;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            IsLoading = true;
            try
            {
                await Task.Delay(400, token);
                var selectedEntityId = (SelectedNode as EntityNodeViewModel)?.Entity.Id;
                RefreshFilesList();

                if (!string.IsNullOrEmpty(selectedEntityId))
                {
                    var found = FindEntityNode(Categories, selectedEntityId);
                    if (found != null)
                    {
                        SelectedNode = found;
                        if (App.Current?.Services?.GetService(typeof(IWorkspaceState)) is IWorkspaceState workspaceState)
                            await workspaceState.ReloadActiveFileContentAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    IsLoading = false;
            }
        });
    }

    private static string GetCategoryKey(VaultEntity entity)
    {
        var parts = entity.RelativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : entity.EntityType + "s";
    }

    private static void SyncFolderChildren(
        ExplorerNodeViewModel parent,
        string parentPath,
        IReadOnlyList<VaultEntity> entities,
        string categoryKey,
        IReadOnlyDictionary<string, VaultEntitySyncPlan> syncPlans,
        HashSet<string> dirtyPaths,
        string? vaultPath)
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
                .Where(e => string.Equals(GetChildFolderName(e, parentPath, categoryKey), folderName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            SyncFolderChildren(folder, folderPath, folderEntities, categoryKey, syncPlans, dirtyPaths, vaultPath);
        }

        foreach (var entity in directEntities.OrderBy(e => VaultEntityDisplay.GetDisplayName(e, vaultPath),
                     StringComparer.OrdinalIgnoreCase))
        {
            syncPlans.TryGetValue(entity.Id, out var plan);
            var syncState = plan?.State
                            ?? (entity.HasValidFrontmatter ? VaultSyncState.LocalOnly : VaultSyncState.Invalid);
            var hasLocal = !string.IsNullOrWhiteSpace(entity.ContentHash);
            var isGitDirty = dirtyPaths.Contains(NormalizePath(entity.RelativePath));

            var existing = parent.Children.OfType<EntityNodeViewModel>()
                .FirstOrDefault(n => string.Equals(n.Entity.Id, entity.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                InsertEntityNode(parent, new EntityNodeViewModel(entity, syncState, isGitDirty, hasLocal, vaultPath));
            }
            else
            {
                existing.SyncFrom(entity, syncState, isGitDirty, hasLocal, vaultPath);
            }
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string? GetChildFolderName(VaultEntity entity, string parentPath, string categoryKey)
    {
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
        HashSet<string> expectedChildIds) =>
        child switch
        {
            FolderNodeViewModel folder => expectedChildIds.Contains($"folder:{folder.FolderPath}"),
            EntityNodeViewModel entity => expectedChildIds.Contains($"entity:{entity.Entity.Id}"),
            _ => false
        };

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

    private static EntityNodeViewModel? FindEntityNode(IEnumerable<ExplorerNodeViewModel> nodes, string entityId)
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
        StopWatcher();
    }
}