using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Vault.Canonical;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Grpc;
using Grpc.Core;

namespace CampaignVault.Authoring.Vault.Sync;

public sealed class VaultSyncEngine
{
    private readonly EntityCanonicalizer _canonicalizer = new();
    private readonly VaultRemoteCache _remoteCache = new();

    private string _vaultPath = string.Empty;
    private VaultGitRepository? _git;
    private string? _campaignName;
    private Func<CampaignSync.CampaignSyncClient>? _clientFactory;
    private CampaignAuthoringSettings? _settings;
    private RemoteCacheManifestReadResult _manifestReadResult = new(null, false, null);

    public VaultConnectionStatus Connection { get; private set; } =
        new(VaultConnectionState.Unknown);

    private VaultMetadata? _metadata;

    public void Bind(
        string vaultPath,
        VaultGitRepository git,
        VaultMetadata metadata,
        Func<CampaignSync.CampaignSyncClient>? clientFactory,
        CampaignAuthoringSettings? settings)
    {
        _vaultPath = vaultPath;
        _git = git;
        _metadata = metadata;
        _campaignName = metadata.CampaignName;
        _clientFactory = clientFactory;
        _settings = settings;
        _remoteCache.Initialize(vaultPath);
        RefreshManifestState();
    }

    public void Unbind()
    {
        _vaultPath = string.Empty;
        _git = null;
        _campaignName = null;
        _clientFactory = null;
        _settings = null;
        _manifestReadResult = new RemoteCacheManifestReadResult(null, false, null);
        Connection = new VaultConnectionStatus(VaultConnectionState.Unknown);
    }

    public async Task FetchAsync(CancellationToken cancellationToken = default)
    {
        EnsureBound();
        EnsureClientConfigured();

        if (string.IsNullOrWhiteSpace(_campaignName))
            throw new VaultException("Vault metadata is missing campaignName.");

        try
        {
            var client = _clientFactory!();
            var response = await client.GetCampaignEntitiesAsync(
                new GetCampaignEntitiesRequest { CampaignName = _campaignName },
                cancellationToken: cancellationToken);

            await _remoteCache.WriteFetchResultAsync(_campaignName, response.Entities);
            RefreshManifestState();

            Connection = new VaultConnectionStatus(
                VaultConnectionState.Online,
                $"Fetched {response.Entities.Count} entities from Campaign Vault.",
                DateTimeOffset.UtcNow);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            Connection = new VaultConnectionStatus(
                VaultConnectionState.Offline,
                $"Campaign Vault is unavailable: {ex.Status.Detail}",
                DateTimeOffset.UtcNow);
            throw new VaultException(Connection.Message!, ex);
        }
        catch (RpcException ex)
        {
            Connection = new VaultConnectionStatus(
                VaultConnectionState.Error,
                $"gRPC error ({ex.StatusCode}): {ex.Status.Detail}",
                DateTimeOffset.UtcNow);
            throw new VaultException(Connection.Message!, ex);
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            Connection = new VaultConnectionStatus(
                VaultConnectionState.Error,
                $"Fetch failed: {ex.Message}",
                DateTimeOffset.UtcNow);
            throw new VaultException(Connection.Message!, ex);
        }
    }

    public async Task SyncCampaignMetadataAsync()
    {
        EnsureBound();
        EnsureClientConfigured();

        if (_metadata == null || string.IsNullOrWhiteSpace(_campaignName))
            return;

        try
        {
            var client = _clientFactory!();
            var request = new UpdateCampaignMetadataRequest
            {
                CampaignName = _campaignName,
                DisplayName = _metadata.DisplayName ?? string.Empty,
            };
            request.NarrativeFocus.AddRange(_metadata.NarrativeFocus ?? []);

            var response = await client.UpdateCampaignMetadataAsync(request);
            if (!response.Success)
            {
                throw new VaultException($"Failed to sync campaign metadata: {response.Message}");
            }
        }
        catch (Exception ex) when (ex is not VaultException)
        {
            throw new VaultException($"Campaign metadata sync failed: {ex.Message}", ex);
        }
    }

    public async Task PushAsync(IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default)
    {
        EnsureBound();
        EnsureClientConfigured();
        RequireCleanWorkingTree();

        var filter = entityIds?.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isFullPush = filter == null || filter.Count == 0;

        if (isFullPush && EvaluateAllPlans().Any(p => p.State == VaultSyncState.Conflict))
        {
            throw new VaultException("Cannot push while entities are in Conflict state. Resolve conflicts first.");
        }

        var pushPlan = GetPushPlan();
        var items = filter == null
            ? pushPlan
            : pushPlan.Where(p => filter.Contains(p.EntityId)).ToList();

        if (items.Count == 0)
            return;

        if (items.Any(p => p.State == VaultSyncState.Conflict))
        {
            throw new VaultException("Cannot push entities in Conflict state. Resolve conflicts first.");
        }

        var client = _clientFactory!();
        var failures = new List<string>();

        // Refresh remote cache once before push (not per-item): catches any simulation
        // activity since the last Fetch without doing an O(N) full-campaign re-fetch.
        Dictionary<string, VaultEntitySyncPlan>? currentPlanById = null;
        if (items.Any(p => p.State != VaultSyncState.DeletedLocally))
        {
            var refreshResponse = await client.GetCampaignEntitiesAsync(
                new GetCampaignEntitiesRequest { CampaignName = _campaignName },
                cancellationToken: cancellationToken);
            await _remoteCache.WriteFetchResultAsync(_campaignName!, refreshResponse.Entities);
            RefreshManifestState();

            currentPlanById = EvaluateAllPlans()
                .ToDictionary(p => p.EntityId, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var item in items)
        {
            try
            {
                if (item.State == VaultSyncState.DeletedLocally)
                {
                    var response = await client.DeleteCampaignEntityAsync(new DeleteCampaignEntityRequest
                    {
                        CampaignName = _campaignName,
                        Id = item.EntityId,
                        Type = item.EntityType
                    }, cancellationToken: cancellationToken);

                    if (!response.Success)
                        failures.Add($"{item.EntityId}: {response.Message}");
                    continue;
                }

                currentPlanById!.TryGetValue(item.EntityId, out var currentPlan);

                if (currentPlan == null || currentPlan.State == VaultSyncState.Conflict ||
                    currentPlan.State == VaultSyncState.BehindVault)
                {
                    failures.Add($"{item.EntityId}: remote has changed since last Fetch — likely simulation activity — re-review before pushing");
                    continue;
                }

                var content = ReadLocalEntityContent(item);
                if (content == null)
                {
                    failures.Add($"{item.EntityId}: local content not found.");
                    continue;
                }

                var pushJson = _canonicalizer.MarkdownToPushJson(item.EntityType, content, _campaignName!);
                var pushResponse = await client.PushCampaignEntityAsync(new PushCampaignEntityRequest
                {
                    CampaignName = _campaignName,
                    Id = item.EntityId,
                    Type = item.EntityType,
                    Content = pushJson
                }, cancellationToken: cancellationToken);

                if (!pushResponse.Success)
                    failures.Add($"{item.EntityId}: {pushResponse.Message}");
            }
            catch (Exception ex) when (ex is not VaultException)
            {
                failures.Add($"{item.EntityId}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
            throw new VaultException($"Push failed for {failures.Count} entity(s): {string.Join("; ", failures)}");

        await UpdateRemoteCacheAfterPushAsync(items);

        if (isFullPush && items.Count == pushPlan.Count)
            AdvanceSyncedRefToHead();
    }

    private async Task UpdateRemoteCacheAfterPushAsync(IReadOnlyList<VaultEntitySyncPlan> pushedItems)
    {
        foreach (var item in pushedItems)
        {
            if (item.State == VaultSyncState.DeletedLocally)
            {
                var path = _remoteCache.GetEntityCachePath(item.EntityId);
                if (File.Exists(path))
                    File.Delete(path);
                continue;
            }

            var content = ReadLocalEntityContent(item);
            if (content == null)
                continue;

            var markdown = _canonicalizer.NormalizeToCanonicalMarkdown(item.EntityType, content);
            var entityPath = _remoteCache.GetEntityCachePath(item.EntityId);
            Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
            await File.WriteAllTextAsync(entityPath, markdown);
        }

        if (pushedItems.Count > 0 && !string.IsNullOrWhiteSpace(_campaignName))
        {
            var manifest = _remoteCache.ReadManifest().Manifest;
            if (manifest != null)
            {
                manifest.FetchedAt = DateTimeOffset.UtcNow;
                var manifestPath = Path.Combine(_remoteCache.CacheRootPath, VaultRemoteCache.ManifestFileName);
                await File.WriteAllTextAsync(
                    manifestPath,
                    System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }) + "\n");
            }
        }
    }

    public async Task PullAsync(IEnumerable<string>? entityIds = null, CancellationToken cancellationToken = default)
    {
        EnsureBound();
        RequireCleanWorkingTree("Pull");

        var filter = entityIds?.Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isFullPull = filter == null || filter.Count == 0;

        var pullPlan = GetPullPlan()
            .Where(p => p.State != VaultSyncState.Conflict)
            .ToList();

        var items = filter == null
            ? pullPlan
            : pullPlan.Where(p => filter.Contains(p.EntityId)).ToList();

        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            if (item.State == VaultSyncState.DeletedRemotely)
            {
                if (!string.IsNullOrWhiteSpace(item.RelativePath))
                {
                    var absolute = Path.Combine(_vaultPath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(absolute))
                        File.Delete(absolute);
                }

                continue;
            }

            if (!_remoteCache.TryReadEntityMarkdown(item.EntityId, out var markdown, out _))
                throw new VaultException($"Remote cache is missing entity '{item.EntityId}'. Run Fetch first.");

            var relativePath = item.RelativePath ?? InferRelativePathFromId(item.EntityId);
            var absolutePath = Path.Combine(_vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            await File.WriteAllTextAsync(absolutePath, markdown);
        }

        _git!.Commit("Pull from Campaign Vault");

        if (isFullPull && !GetPullPlan().Any(p => p.State is VaultSyncState.Conflict or VaultSyncState.BehindVault or VaultSyncState.RemoteOnly or VaultSyncState.DeletedRemotely))
            AdvanceSyncedRefToHead();
    }

    public async Task ResolveConflictAsync(string entityId, ConflictResolution resolution, string? mergedContent = null)
    {
        EnsureBound();

        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Entity id is required.", nameof(entityId));

        var plan = EvaluateAllPlans().FirstOrDefault(p =>
            string.Equals(p.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

        if (plan == null)
            throw new VaultException($"Entity '{entityId}' was not found in the sync evaluation.");

        if (plan.State != VaultSyncState.Conflict)
            throw new VaultException($"Entity '{entityId}' is not in Conflict state (current: {plan.State}).");

        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                await PushAsync([entityId]);
                break;

            case ConflictResolution.KeepVault:
                if (!_remoteCache.TryReadEntityMarkdown(entityId, out var remoteMarkdown, out _))
                    throw new VaultException($"Remote cache is missing entity '{entityId}'. Run Fetch first.");

                await WriteEntityFileAsync(plan, remoteMarkdown);
                break;

            case ConflictResolution.Merged:
                if (string.IsNullOrWhiteSpace(mergedContent))
                    throw new ArgumentException("Merged content is required for Merged resolution.", nameof(mergedContent));

                await WriteEntityFileAsync(plan, mergedContent);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(resolution));
        }
    }

    public VaultSyncSummary GetSyncSummary()
    {
        var plans = EvaluateAllPlans();

        return new VaultSyncSummary(
            SyncedCount: plans.Count(p => p.State == VaultSyncState.Synced),
            AheadCount: plans.Count(p => p.State == VaultSyncState.AheadOfVault),
            BehindCount: plans.Count(p => p.State == VaultSyncState.BehindVault),
            ConflictCount: plans.Count(p => p.State == VaultSyncState.Conflict),
            LocalOnlyCount: plans.Count(p => p.State == VaultSyncState.LocalOnly),
            RemoteOnlyCount: plans.Count(p => p.State == VaultSyncState.RemoteOnly),
            DeletedLocallyCount: plans.Count(p => p.State == VaultSyncState.DeletedLocally),
            DeletedRemotelyCount: plans.Count(p => p.State == VaultSyncState.DeletedRemotely),
            InvalidCount: plans.Count(p => p.State == VaultSyncState.Invalid),
            AbsentCount: plans.Count(p => p.State == VaultSyncState.Absent),
            Connection: Connection,
            LastFetchedAt: _manifestReadResult.Manifest?.FetchedAt,
            RemoteCacheCorrupt: _manifestReadResult.IsCorrupt);
    }

    public IReadOnlyList<VaultEntitySyncPlan> GetPushPlan()
    {
        EnsureBound();
        var headSha = _git!.GetHeadSha();
        var syncedSha = _git.GetSyncedCommitSha();
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(syncedSha))
            return [];

        if (headSha == syncedSha)
            return [];

        var changedPaths = _git.GetChangedEntityPathsBetween(syncedSha, headSha);
        var plans = EvaluateAllPlans();
        var planById = plans.ToDictionary(p => p.EntityId, StringComparer.OrdinalIgnoreCase);

        var pushPlans = new List<VaultEntitySyncPlan>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in changedPaths)
        {
            var entityType = VaultPaths.EntityTypeFromRelativePath(path);
            if (entityType == null)
                continue;

            var id = InferIdFromPath(path, entityType);
            if (!planById.TryGetValue(id, out var plan))
                continue;

            if (plan.State is VaultSyncState.AheadOfVault
                or VaultSyncState.LocalOnly
                or VaultSyncState.DeletedLocally)
            {
                pushPlans.Add(plan);
                included.Add(id);
            }
        }

        foreach (var plan in plans.Where(p =>
                     p.State == VaultSyncState.DeletedLocally && !included.Contains(p.EntityId)))
        {
            pushPlans.Add(plan);
            included.Add(plan.EntityId);
        }

        return pushPlans
            .OrderBy(p => p.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<VaultEntitySyncPlan> GetEntitySyncPlans() => EvaluateAllPlans();

    public IReadOnlyList<VaultEntitySyncPlan> GetPullPlan()
    {
        return EvaluateAllPlans()
            .Where(p => p.State is VaultSyncState.BehindVault
                or VaultSyncState.RemoteOnly
                or VaultSyncState.Conflict
                or VaultSyncState.DeletedRemotely)
            .OrderBy(p => p.EntityType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<VaultEntitySyncPlan> EvaluateAllPlans()
    {
        EnsureBound();
        RefreshManifestState();

        var headSha = _git!.GetHeadSha();
        var syncedSha = _git.GetSyncedCommitSha();
        var remoteIndex = _remoteCache.ReadEntityIndex(out _manifestReadResult);
        var localIndex = BuildLocalEntityIndex(headSha);
        var baseIndex = BuildCommitEntityIndex(syncedSha);

        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in localIndex.Keys) allIds.Add(id);
        foreach (var id in baseIndex.Keys) allIds.Add(id);
        foreach (var id in remoteIndex.Keys) allIds.Add(id);

        var plans = new List<VaultEntitySyncPlan>();
        foreach (var id in allIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            localIndex.TryGetValue(id, out var local);
            baseIndex.TryGetValue(id, out var baseEntry);
            remoteIndex.TryGetValue(id, out var remote);

            if (local?.ParseError != null)
            {
                plans.Add(new VaultEntitySyncPlan(
                    id,
                    local.EntityType,
                    local.RelativePath,
                    VaultSyncState.Invalid,
                    ParseError: local.ParseError));
                continue;
            }

            var entityType = local?.EntityType
                             ?? baseEntry?.EntityType
                             ?? remote?.Type
                             ?? "unknown";

            var relativePath = local?.RelativePath ?? baseEntry?.RelativePath;

            var localHash = local?.CanonicalHash;
            var baseHash = baseEntry?.CanonicalHash;
            var remoteHash = remote?.CanonicalHash;

            if (remote == null && _remoteCache.TryReadEntityMarkdown(id, out _, out var computedRemoteHash))
                remoteHash = computedRemoteHash;

            var state = ClassifySyncState(localHash, baseHash, remoteHash);
            plans.Add(new VaultEntitySyncPlan(
                id,
                entityType,
                relativePath,
                state,
                localHash,
                baseHash,
                remoteHash));
        }

        return plans;
    }

    private Dictionary<string, EntityIndexEntry> BuildLocalEntityIndex(string? headSha)
    {
        var index = new Dictionary<string, EntityIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var paths = CollectLocalEntityPaths(headSha);

        foreach (var (relativePath, entityType) in paths)
        {
            string? content = null;
            if (File.Exists(Path.Combine(_vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar))))
                content = File.ReadAllText(Path.Combine(_vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (content == null)
            {
                index[InferIdFromPath(relativePath, entityType)] = new EntityIndexEntry(
                    InferIdFromPath(relativePath, entityType),
                    entityType,
                    relativePath,
                    null,
                    null);
                continue;
            }

            try
            {
                var canonicalHash = _canonicalizer.ComputeCanonicalHash(entityType, content);
                var id = ReadEntityId(content, relativePath, entityType);
                index[id] = new EntityIndexEntry(id, entityType, relativePath, canonicalHash, null);
            }
            catch (Exception ex)
            {
                var id = ReadEntityId(content, relativePath, entityType);
                index[id] = new EntityIndexEntry(id, entityType, relativePath, null, ex.Message);
            }
        }

        return index;
    }

    private HashSet<(string RelativePath, string EntityType)> CollectLocalEntityPaths(string? headSha)
    {
        var paths = new HashSet<(string, string)>(new PathComparer());

        foreach (var (folder, entityType) in VaultPaths.EntityFolders)
        {
            var folderPath = Path.Combine(_vaultPath, folder);
            if (Directory.Exists(folderPath))
            {
                foreach (var absolutePath in Directory.EnumerateFiles(folderPath, "*.md", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(_vaultPath, absolutePath).Replace('\\', '/');
                    paths.Add((relativePath, entityType));
                }
            }

            if (!string.IsNullOrWhiteSpace(headSha))
            {
                foreach (var relativePath in _git!.GetEntityPathsAtCommit(headSha)
                             .Where(p => VaultPaths.EntityTypeFromRelativePath(p) == entityType))
                {
                    paths.Add((relativePath, entityType));
                }
            }
        }

        return paths;
    }

    private Dictionary<string, EntityIndexEntry> BuildCommitEntityIndex(string? commitSha)
    {
        var index = new Dictionary<string, EntityIndexEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(commitSha))
            return index;

        foreach (var relativePath in _git!.GetEntityPathsAtCommit(commitSha))
        {
            var entityType = VaultPaths.EntityTypeFromRelativePath(relativePath);
            if (entityType == null)
                continue;

            var content = _git.TryReadFileAtCommit(commitSha, relativePath);
            if (content == null)
                continue;

            try
            {
                var canonicalHash = _canonicalizer.ComputeCanonicalHash(entityType, content);
                var id = ReadEntityId(content, relativePath, entityType);
                index[id] = new EntityIndexEntry(id, entityType, relativePath, canonicalHash, null);
            }
            catch (Exception ex)
            {
                var id = InferIdFromPath(relativePath, entityType);
                index[id] = new EntityIndexEntry(id, entityType, relativePath, null, ex.Message);
            }
        }

        return index;
    }

    private static VaultSyncState ClassifySyncState(string? localHash, string? baseHash, string? remoteHash)
    {
        var hasLocal = !string.IsNullOrWhiteSpace(localHash);
        var hasBase = !string.IsNullOrWhiteSpace(baseHash);
        var hasRemote = !string.IsNullOrWhiteSpace(remoteHash);

        if (!hasLocal && !hasRemote)
            return VaultSyncState.Absent;

        if (!hasLocal && hasRemote)
            return hasBase ? VaultSyncState.DeletedLocally : VaultSyncState.RemoteOnly;

        if (hasLocal && !hasRemote)
            return hasBase ? VaultSyncState.DeletedRemotely : VaultSyncState.LocalOnly;

        if (localHash == remoteHash)
            return VaultSyncState.Synced;

        if (hasBase)
        {
            if (localHash == baseHash && remoteHash != baseHash)
                return VaultSyncState.BehindVault;

            if (localHash != baseHash && remoteHash == baseHash)
                return VaultSyncState.AheadOfVault;

            if (localHash != baseHash && remoteHash != baseHash && localHash != remoteHash)
                return VaultSyncState.Conflict;
        }

        return VaultSyncState.Conflict;
    }

    private string? ReadLocalEntityContent(VaultEntitySyncPlan item)
    {
        if (!string.IsNullOrWhiteSpace(item.RelativePath))
        {
            var absolute = Path.Combine(_vaultPath, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absolute))
                return File.ReadAllText(absolute);
        }

        var headSha = _git!.GetHeadSha();
        if (!string.IsNullOrWhiteSpace(item.RelativePath) && !string.IsNullOrWhiteSpace(headSha))
            return _git.TryReadFileAtCommit(headSha, item.RelativePath);

        return null;
    }

    private async Task WriteEntityFileAsync(VaultEntitySyncPlan plan, string markdown)
    {
        var relativePath = plan.RelativePath ?? InferRelativePathFromId(plan.EntityId);
        var absolutePath = Path.Combine(_vaultPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, markdown);
    }

    private static string InferRelativePathFromId(string entityId) => $"{entityId}.md";

    private void AdvanceSyncedRefToHead()
    {
        var head = _git!.GetHeadSha()
                   ?? throw new VaultException("Cannot advance refs/cv/synced because HEAD is missing.");
        _git.SetSyncedCommit(head);
    }

    private void RequireCleanWorkingTree(string action = "Push")
    {
        if (_git!.GetWorkingTreeStatus().IsDirty)
        {
            throw new VaultException(
                $"{action} requires a clean working tree. Commit or discard local changes, then {action.ToLowerInvariant()}.");
        }
    }

    private void EnsureClientConfigured()
    {
        if (_clientFactory == null)
        {
            Connection = new VaultConnectionStatus(
                VaultConnectionState.Offline,
                "Vault sync is not configured. Set gRPC host and port in settings.",
                DateTimeOffset.UtcNow);
            throw new VaultException(Connection.Message!);
        }
    }

    private void RefreshManifestState()
    {
        _manifestReadResult = _remoteCache.ReadManifest();
        if (_manifestReadResult.IsCorrupt)
        {
            Connection = new VaultConnectionStatus(
                VaultConnectionState.Error,
                _manifestReadResult.ErrorMessage ?? "Remote cache manifest is corrupt.",
                DateTimeOffset.UtcNow);
        }
    }

    private static string ReadEntityId(string content, string relativePath, string entityType)
    {
        if (VaultFrontmatter.TryReadId(content, out var id) && !string.IsNullOrWhiteSpace(id))
            return id!;
        return VaultFrontmatter.InferIdFromRelativePath(relativePath, entityType);
    }

    private static string InferIdFromPath(string relativePath, string entityType) =>
        VaultFrontmatter.InferIdFromRelativePath(relativePath, entityType);

    private void EnsureBound()
    {
        if (_git == null || string.IsNullOrWhiteSpace(_vaultPath))
            throw new InvalidOperationException("Vault sync engine is not bound to an open vault.");
    }

    private sealed record EntityIndexEntry(
        string Id,
        string EntityType,
        string RelativePath,
        string? CanonicalHash,
        string? ParseError);

    private sealed class PathComparer : IEqualityComparer<(string RelativePath, string EntityType)>
    {
        public bool Equals((string RelativePath, string EntityType) x, (string RelativePath, string EntityType) y) =>
            string.Equals(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.EntityType, y.EntityType, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string RelativePath, string EntityType) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RelativePath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EntityType));
    }
}