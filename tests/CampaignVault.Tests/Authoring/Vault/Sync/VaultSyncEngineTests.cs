using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Grpc;
using Grpc.Core;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests.Authoring.Vault.Sync;

public sealed class VaultSyncEngineTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();
    private readonly CampaignSync.CampaignSyncClient _mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();

    public VaultSyncEngineTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "cv_sync_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _session.Dispose();
        TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public async Task FetchAsync_WritesRemoteCacheAndManifest()
    {
        await CreateVaultAsync();
        ConfigureMockClient();
        await _session.FetchAsync();

        var cacheRoot = Path.Combine(_tempDirectory, VaultPaths.AppConfigDirectoryName, "remote-cache");
        var manifestPath = Path.Combine(cacheRoot, VaultRemoteCache.ManifestFileName);
        Assert.True(File.Exists(manifestPath));

        var entityPath = Path.Combine(cacheRoot, "entities", "characters", "grog.md");
        Assert.True(File.Exists(entityPath));

        var summary = _session.GetSyncSummary();
        Assert.NotNull(summary.LastFetchedAt);
        Assert.Equal(VaultConnectionState.Online, summary.Connection.State);
    }

    [Fact]
    public async Task FetchAsync_SecondFetch_UpdatesManifestTimestamp()
    {
        await CreateVaultAsync();
        ConfigureMockClient();

        await _session.FetchAsync();
        var first = File.ReadAllText(Path.Combine(_tempDirectory, VaultPaths.AppConfigDirectoryName, "remote-cache", VaultRemoteCache.ManifestFileName));

        await Task.Delay(50);
        await _session.FetchAsync();
        var second = File.ReadAllText(Path.Combine(_tempDirectory, VaultPaths.AppConfigDirectoryName, "remote-cache", VaultRemoteCache.ManifestFileName));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task GetSyncSummary_WhenSyncedAndRemoteMatches_ReportsSynced()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Brave warrior.
            """);
        await AdvanceSyncedToHeadAsync();

        ConfigureMockClientWithMatchingRemote();
        await _session.FetchAsync();

        var summary = _session.GetSyncSummary();
        Assert.Equal(1, summary.SyncedCount);
        Assert.Equal(0, summary.ConflictCount);
        Assert.Equal(0, summary.AheadCount);
    }

    [Fact]
    public async Task GetPushPlan_WhenAheadOfVault_IncludesEntity()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Brave warrior.
            """);

        ConfigureMockClientWithMatchingRemote();
        await _session.FetchAsync();
        await AdvanceSyncedToHeadAsync();

        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Updated ahead notes.
            """);

        var pushPlan = _session.GetPushPlan();
        var item = Assert.Single(pushPlan);
        Assert.Equal("characters/grog", item.EntityId);
        Assert.Equal(VaultSyncState.AheadOfVault, item.State);
    }

    [Fact]
    public async Task GetPullPlan_WhenBehindVault_IncludesEntity()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Local base copy.
            """);
        await AdvanceSyncedToHeadAsync();

        ConfigureMockClientWithDifferentRemote();
        await _session.FetchAsync();

        var pullPlan = _session.GetPullPlan();
        var item = Assert.Single(pullPlan);
        Assert.Equal("characters/grog", item.EntityId);
        Assert.Equal(VaultSyncState.BehindVault, item.State);
    }

    [Fact]
    public async Task GetSyncSummary_WhenConflict_ReportsConflict()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Shared base.
            """);

        ConfigureMockClientWithMatchingRemote();
        await _session.FetchAsync();
        await AdvanceSyncedToHeadAsync();

        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Local diverged.
            """);

        ConfigureMockClientWithDifferentRemote();
        await _session.FetchAsync();

        var summary = _session.GetSyncSummary();
        Assert.Equal(1, summary.ConflictCount);
    }

    [Fact]
    public async Task GetPushPlan_WhenSyncedEqualsHead_IsEmpty()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync("""
            ---
            id: characters/grog
            name: Grog
            currentHp: 10
            maxHp: 20
            ---
            Same everywhere.
            """);

        ConfigureMockClientWithMatchingRemote();
        await _session.FetchAsync();
        await AdvanceSyncedToHeadAsync();

        Assert.Empty(_session.GetPushPlan());
    }

    [Fact]
    public async Task FetchAsync_WhenGrpcUnavailable_SetsOfflineAndThrows()
    {
        await CreateVaultAsync();
        _session.ConfigureVaultSync(() => _mockClient);

        var call = CreateFakeUnaryCall<EntityListResponse>(
            Task.FromException<EntityListResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call);

        var ex = await Assert.ThrowsAsync<VaultException>(() => _session.FetchAsync());
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VaultConnectionState.Offline, _session.VaultConnection.State);
    }

    private async Task CreateVaultAsync()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e");
        _session.ConfigureVaultSync(() => _mockClient);
    }

    private void ConfigureMockClient()
    {
        ConfigureMockClientWithMatchingRemote();
    }

    private void ConfigureMockClientWithMatchingRemote()
    {
        var json = """
            {
              "id": "characters/grog",
              "name": "Grog",
              "currentHp": 10,
              "maxHp": 20,
              "notes": "Brave warrior."
            }
            """;

        SetupRemoteEntities(("characters/grog", "character", json));
    }

    private void ConfigureMockClientWithDifferentRemote()
    {
        var json = """
            {
              "id": "characters/grog",
              "name": "Grog",
              "currentHp": 10,
              "maxHp": 20,
              "notes": "Remote diverged copy."
            }
            """;

        SetupRemoteEntities(("characters/grog", "character", json));
    }

    private void SetupRemoteEntities(params (string Id, string Type, string Json)[] entities)
    {
        var response = new EntityListResponse();
        foreach (var (id, type, json) in entities)
        {
            response.Entities.Add(new EntityItem
            {
                Id = id,
                Type = type,
                Content = json
            });
        }

        var call = CreateFakeUnaryCall(response);

        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call);
        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<CallOptions>())
            .Returns(call);
    }

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(TResponse response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(Task<TResponse> responseTask) =>
        new(
            responseTask,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private async Task WriteAndCommitEntityAsync(string markdown)
    {
        var path = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, markdown);

        using var repo = new LibGit2Sharp.Repository(_tempDirectory);
        LibGit2Sharp.Commands.Stage(repo, "characters/grog.md");
        var sig = new LibGit2Sharp.Signature("Test", "test@test", DateTimeOffset.UtcNow);
        repo.Commit("test commit", sig, sig, new LibGit2Sharp.CommitOptions());

        await ReopenSessionAsync();
    }

    private async Task AdvanceSyncedToHeadAsync()
    {
        using var git = new VaultGitRepository();
        git.Open(_tempDirectory);
        git.SetSyncedCommit(git.GetHeadSha()!);
        await ReopenSessionAsync();
    }

    private async Task ReopenSessionAsync()
    {
        await _session.CloseAsync();
        await _session.OpenAsync(_tempDirectory);
        _session.ConfigureVaultSync(() => _mockClient);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}