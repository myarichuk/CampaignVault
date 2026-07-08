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

    public Task FetchAsync() => WithLockAsync(() => _syncEngine.FetchAsync());

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

    public Task PushAsync(IEnumerable<string>? entityIds = null) =>
        WithLockAsync(() => _syncEngine.PushAsync(entityIds));

    public Task PullAsync(IEnumerable<string>? entityIds = null) =>
        WithLockAsync(() => _syncEngine.PullAsync(entityIds));

    public Task ResolveConflictAsync(string entityId, ConflictResolution resolution, string? mergedContent = null) =>
        WithLockAsync(() => _syncEngine.ResolveConflictAsync(entityId, resolution, mergedContent));

    public Task<(string RelativePath, string Content)> CreateEntityAsync(string entityType, string name) =>
        WithLockAsync(async () =>
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Entity name is required.", nameof(name));

            var normalizedType = entityType?.Trim().ToLowerInvariant() ?? "";
            if (!EntityCreation.IsSupportedEntityType(normalizedType))
                throw new VaultException($"Unsupported entity type '{entityType}'.");

            var (relativePath, slug) = EntityCreation.BuildNewEntityPath(normalizedType, name, DateTime.Now,
                relativePathExists: rel => File.Exists(Path.Combine(VaultPath!, rel.Replace('/', Path.DirectorySeparatorChar))));
            var template = _canonicalizer.GetBlankTemplate(normalizedType, slug, name);
            await WriteEntityFileUnlockedAsync(relativePath, template);
            return (relativePath, template);
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