using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault.Canonical;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Grpc;

namespace CampaignVault.Authoring.Vault;

public sealed class CampaignVaultSession : IDisposable
{
    private readonly MetadataService _metadataService = new();
    private readonly VaultCatalog _catalog = new();
    private readonly VaultBootstrap _bootstrap = new();
    private readonly VaultSyncEngine _syncEngine = new();
    private readonly EntityCanonicalizer _canonicalizer = new();
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private VaultGitRepository? _git;
    private Func<CampaignSync.CampaignSyncClient>? _clientFactory;
    private CampaignAuthoringSettings? _syncSettings;

    public string? VaultPath { get; private set; }

    public VaultMetadata? Metadata { get; private set; }

    public bool IsOpen => VaultPath != null;

    public string? HeadCommitSha => _git?.GetHeadSha();

    public string? SyncedCommitSha => _git?.GetSyncedCommitSha();

    public VaultConnectionStatus VaultConnection => _syncEngine.Connection;

    public bool IsVaultSyncConfigured => _clientFactory != null;

    public void ConfigureVaultSync(
        Func<CampaignSync.CampaignSyncClient>? clientFactory,
        CampaignAuthoringSettings? settings = null)
    {
        _clientFactory = clientFactory;
        _syncSettings = settings;
        if (IsOpen && Metadata != null && _git != null)
        {
            _syncEngine.Bind(VaultPath!, _git, Metadata, _clientFactory, _syncSettings);
        }
    }

    public async Task<VaultMetadata> CreateAsync(
        string vaultPath,
        string campaignName,
        string? ruleset = null,
        string? displayName = null,
        List<string>? narrativeFocus = null)
    {
        await CloseAsync();
        var metadata = await _bootstrap.CreateAsync(vaultPath, campaignName, ruleset, displayName, narrativeFocus);
        await OpenAsync(vaultPath);
        return metadata;
    }

    public async Task OpenAsync(string vaultPath)
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            throw new ArgumentException("Vault path is required.", nameof(vaultPath));

        var fullPath = Path.GetFullPath(vaultPath);
        if (!Directory.Exists(fullPath))
            throw new VaultException($"Vault directory not found: '{fullPath}'.");

        var metadataPath = Path.Combine(fullPath, VaultPaths.MetadataFileName);
        if (!File.Exists(metadataPath))
            throw new VaultException($"Missing {VaultPaths.MetadataFileName}. This folder is not a Campaign Vault.");

        if (!VaultGitRepository.IsGitRepository(fullPath))
            throw new VaultException("Missing local git repository. Campaign vaults require a .git directory.");

        var metadata = await _metadataService.LoadMetadataAsync(fullPath);
        if (metadata == null)
            throw new VaultException($"Missing or invalid {VaultPaths.MetadataFileName}.");

        await CloseAsync();

        _git = new VaultGitRepository();
        _git.Open(fullPath);
        EnsureOrInitSyncedRef();
        MetadataService.ValidateMetadata(metadata);

        VaultPath = fullPath;
        Metadata = metadata;
        _syncEngine.Bind(fullPath, _git, metadata, _clientFactory, _syncSettings);
    }

    public IReadOnlyList<VaultEntity> ScanEntities()
    {
        EnsureOpen();
        return _catalog.Scan(VaultPath!);
    }

    public GitWorkingTreeStatus GetGitStatus()
    {
        EnsureOpen();
        return _git!.GetWorkingTreeStatus();
    }

    public Task FetchAsync(CancellationToken cancellationToken = default) => WithLockAsync(() => _syncEngine.FetchAsync(cancellationToken));

    public VaultSyncSummary GetSyncSummary() => _syncEngine.GetSyncSummary();

    public IReadOnlyList<VaultEntitySyncPlan> GetPushPlan() => _syncEngine.GetPushPlan();

    public IReadOnlyList<VaultEntitySyncPlan> GetPullPlan() => _syncEngine.GetPullPlan();

    public IReadOnlyList<VaultEntitySyncPlan> GetEntitySyncPlans() => _syncEngine.GetEntitySyncPlans();

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action)
    {
        await _mutationLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private Task WithLockAsync(Func<Task> action) =>
        WithLockAsync(async () => { await action(); return true; });

    public Task DiscardChangesAsync() =>
        WithLockAsync(() =>
        {
            EnsureOpen();
            _git!.DiscardChanges();
            return Task.CompletedTask;
        });

    public Task CommitAsync(string message) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Commit message is required.", nameof(message));

            _git!.Commit(message);
        });

    public Task<string> ReadFileAsync(string relativePath)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/');
        var absolute = Path.Combine(VaultPath!, normalized.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
            throw new VaultException($"File not found in vault: '{normalized}'.");

        return File.ReadAllTextAsync(absolute);
    }

    public Task WriteFileAsync(string relativePath, string content) =>
        WithLockAsync(() => WriteEntityFileUnlockedAsync(relativePath, content));

    private async Task WriteEntityFileUnlockedAsync(string relativePath, string content)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path is required.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/');
        var absolute = Path.Combine(VaultPath!, normalized.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, content);
    }

    public Task PushAsync(IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default) =>
        WithLockAsync(() => _syncEngine.PushAsync(entityIds, cancellationToken));

    public Task PullAsync(IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default) =>
        WithLockAsync(() => _syncEngine.PullAsync(entityIds, cancellationToken));

    public Task ResolveConflictAsync(string entityId, ConflictResolution resolution, string? mergedContent = null) =>
        WithLockAsync(() => _syncEngine.ResolveConflictAsync(entityId, resolution, mergedContent));

    public Task<(string RelativePath, string Content)> CreateEntityAsync(string entityType, string name, string? targetSubfolder = null) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Entity name is required.", nameof(name));

            var normalizedType = entityType?.Trim().ToLowerInvariant() ?? "";
            if (!EntityCreation.IsSupportedEntityType(normalizedType))
                throw new VaultException($"Unsupported entity type '{entityType}'.");

            var (relativePath, slug) = EntityCreation.BuildNewEntityPath(normalizedType, name, DateTime.Now,
                relativePathExists: rel => File.Exists(Path.Combine(VaultPath!, rel.Replace('/', Path.DirectorySeparatorChar))),
                targetSubfolder: targetSubfolder);
            var template = _canonicalizer.GetBlankTemplate(normalizedType, slug, name);
            await WriteEntityFileUnlockedAsync(relativePath, template);
            return (relativePath, template);
        });

    public Task<string> RenameEntityAsync(string relativePath, string newName) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New name is required.", nameof(newName));

            var normalizedOld = relativePath.Replace('\\', '/');
            var entityType = VaultPaths.EntityTypeFromRelativePath(normalizedOld)
                ?? throw new VaultException($"Could not determine entity type for '{normalizedOld}'.");

            var content = await ReadFileAsync(normalizedOld);
            if (!VaultFrontmatter.TryReadId(content, out var oldId) || oldId == null)
                throw new VaultException($"Could not read id from '{normalizedOld}'.");

            var lastSlash = oldId.LastIndexOf('/');
            var idPrefix = lastSlash >= 0 ? oldId[..(lastSlash + 1)] : string.Empty;

            var folder = EntityCreation.GetFolderForType(entityType);
            var prefix = folder + "/";
            var withinFolder = normalizedOld.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? normalizedOld[prefix.Length..]
                : normalizedOld;
            var subfolder = Path.GetDirectoryName(withinFolder)?.Replace('\\', '/');
            subfolder = string.IsNullOrEmpty(subfolder) ? null : subfolder;

            var (newRelativePath, newSlug) = EntityCreation.BuildNewEntityPath(
                entityType, newName, DateTime.Now,
                relativePathExists: rel => rel != normalizedOld
                    && File.Exists(Path.Combine(VaultPath!, rel.Replace('/', Path.DirectorySeparatorChar))),
                targetSubfolder: subfolder);

            if (string.Equals(newRelativePath, normalizedOld, StringComparison.OrdinalIgnoreCase))
                return newRelativePath;

            var newId = idPrefix + newSlug;
            var newContent = VaultFrontmatter.ReplaceIdLine(content, newId);

            await WriteEntityFileUnlockedAsync(newRelativePath, newContent);

            var oldAbsolute = Path.Combine(VaultPath!, normalizedOld.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(oldAbsolute))
                File.Delete(oldAbsolute);

            return newRelativePath;
        });

    public Task DeleteEntityFileAsync(string relativePath) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path is required.", nameof(relativePath));

            var normalized = relativePath.Replace('\\', '/');
            var absolute = Path.Combine(VaultPath!, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute))
                File.Delete(absolute);
        });

    public async Task SyncCampaignMetadataAsync()
    {
        EnsureOpen();
        await _syncEngine.SyncCampaignMetadataAsync();
    }

    public Task UpdateMetadataAsync(string? displayName, List<string> narrativeFocus) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            Metadata!.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            Metadata.NarrativeFocus = narrativeFocus;
            await _metadataService.SaveMetadataAsync(VaultPath!, Metadata);
            return true;
        });

    public Task CloseAsync()
    {
        _syncEngine.Unbind();
        _git?.Dispose();
        _git = null;
        VaultPath = null;
        Metadata = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _syncEngine.Unbind();
        _git?.Dispose();
        _git = null;
        VaultPath = null;
        Metadata = null;
    }

    private void EnsureOpen()
    {
        if (!IsOpen)
            throw new InvalidOperationException("No campaign vault is open.");
    }

    private void EnsureOrInitSyncedRef()
    {
        try
        {
            _git!.RequireSyncedRef();
        }
        catch (VaultException)
        {
            // For local/offline authoring (no main MCP), auto-initialize the synced cursor
            // to current HEAD so local commits/edits work without requiring a prior push/fetch.
            var head = _git!.GetHeadSha();
            if (!string.IsNullOrWhiteSpace(head))
            {
                _git.SetSyncedCommit(head);
            }
            else
            {
                // No commits yet (should not happen for valid vaults) - let original error surface on use
                _git.RequireSyncedRef();
            }
        }
    }
}