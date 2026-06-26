using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Grpc;
using Grpc.Core;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests.Authoring.Vault.Sync;

public sealed class VaultSyncPushPullTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();
    private readonly CampaignSync.CampaignSyncClient _mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();

    public VaultSyncPushPullTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "cv_pushpull_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _session.Dispose();
        TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public async Task PushAsync_FullSuccess_AdvancesSyncedRefToHead()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Baseline."));
        SetupSyncMocks(GrogJson("Baseline."));
        await _session.FetchAsync();
        await AdvanceSyncedToHeadAsync();

        await WriteAndCommitEntityAsync(GrogMarkdown("Push me."));
        var headBefore = _session.HeadCommitSha;

        await _session.PushAsync();

        Assert.Equal(headBefore, _session.SyncedCommitSha);
    }

    [Fact]
    public async Task PushAsync_PartialEntity_DoesNotAdvanceSyncedRef()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Baseline."));
        SetupSyncMocks(GrogJson("Baseline."));
        await _session.FetchAsync();
        await AdvanceSyncedToHeadAsync();

        await WriteAndCommitEntityAsync(GrogMarkdown("Push me."));
        var syncedBefore = _session.SyncedCommitSha;

        await _session.PushAsync(["characters/grog"]);

        Assert.Equal(syncedBefore, _session.SyncedCommitSha);
    }

    [Fact]
    public async Task PushAsync_DirtyWorkingTree_Throws()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Clean."));
        await AdvanceSyncedToHeadAsync();
        SetupSyncMocks(GrogJson("Clean."));
        await _session.FetchAsync();

        await WriteEntityOnDiskAsync(GrogMarkdown("Dirty edit."));

        var ex = await Assert.ThrowsAsync<VaultException>(() => _session.PushAsync());
        Assert.Contains("clean working tree", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PushAsync_WhenConflict_Throws()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Base."));
        await AdvanceSyncedToHeadAsync();
        SetupSyncMocks(GrogJson("Base."));
        await _session.FetchAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Local change."));
        SetupSyncMocks(GrogJson("Remote change."));
        await _session.FetchAsync();

        var ex = await Assert.ThrowsAsync<VaultException>(() => _session.PushAsync());
        Assert.Contains("Conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PullAsync_WritesCanonicalMarkdownAndCommits()
    {
        await CreateVaultAsync();
        SetupSyncMocks(GrogJson("Pulled from vault."));
        await _session.FetchAsync();

        await _session.PullAsync();

        var path = Path.Combine(_tempDirectory, "characters", "grog.md");
        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("Pulled from vault.", content);
        Assert.False(string.IsNullOrWhiteSpace(_session.HeadCommitSha));
    }

    [Fact]
    public async Task PushAsync_DeletedLocally_CallsDeleteRpc()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Delete me."));
        await AdvanceSyncedToHeadAsync();
        SetupSyncMocks(GrogJson("Delete me."));
        await _session.FetchAsync();

        File.Delete(Path.Combine(_tempDirectory, "characters", "grog.md"));
        await CommitAllAsync("remove grog");

        var pushPlan = _session.GetPushPlan();
        Assert.Single(pushPlan);
        Assert.Equal(VaultSyncState.DeletedLocally, pushPlan[0].State);

        await _session.PushAsync();

        _mockClient.Received(1).DeleteCampaignEntityAsync(
            Arg.Is<DeleteCampaignEntityRequest>(r =>
                r.Id == "characters/grog" && r.Type == "character"),
            Arg.Any<Metadata>(),
            Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveConflictAsync_KeepVault_WritesRemoteContent()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Shared base."));
        await AdvanceSyncedToHeadAsync();
        SetupSyncMocks(GrogJson("Shared base."));
        await _session.FetchAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Local version."));
        SetupSyncMocks(GrogJson("Vault version."));
        await _session.FetchAsync();

        await _session.ResolveConflictAsync("characters/grog", ConflictResolution.KeepVault);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "characters", "grog.md"));
        Assert.Contains("Vault version.", content);
    }

    private void SetupSyncMocks(string remoteJson)
    {
        var fetchResponse = new EntityListResponse();
        fetchResponse.Entities.Add(new EntityItem
        {
            Id = "characters/grog",
            Type = "character",
            Content = remoteJson
        });

        var fetchCall = CreateFakeUnaryCall(fetchResponse);
        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(fetchCall);
        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<CallOptions>())
            .Returns(fetchCall);

        var pushResponse = new PushResponse { Success = true, Message = "ok" };
        var pushCall = CreateFakeUnaryCall(pushResponse);
        _mockClient
            .PushCampaignEntityAsync(Arg.Any<PushCampaignEntityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(pushCall);
        _mockClient
            .PushCampaignEntityAsync(Arg.Any<PushCampaignEntityRequest>(), Arg.Any<CallOptions>())
            .Returns(pushCall);

        _mockClient
            .DeleteCampaignEntityAsync(Arg.Any<DeleteCampaignEntityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(pushCall);
        _mockClient
            .DeleteCampaignEntityAsync(Arg.Any<DeleteCampaignEntityRequest>(), Arg.Any<CallOptions>())
            .Returns(pushCall);
    }

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(TResponse response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static string GrogJson(string notes) => $$"""
        {"id":"characters/grog","name":"Grog","currentHp":10,"maxHp":20,"notes":"{{notes}}"}
        """;

    private static string GrogMarkdown(string notes) => $$"""
        ---
        id: characters/grog
        name: Grog
        currentHp: 10
        maxHp: 20
        ---
        {{notes}}
        """;

    private async Task CreateVaultAsync()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e");
        _session.ConfigureVaultSync(() => _mockClient);
    }

    private async Task WriteEntityOnDiskAsync(string markdown)
    {
        var path = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, markdown);
        await ReopenSessionAsync();
    }

    private async Task WriteAndCommitEntityAsync(string markdown)
    {
        await WriteEntityOnDiskAsync(markdown);
        await CommitAllAsync("commit");
    }

    private async Task CommitAllAsync(string message)
    {
        using var repo = new LibGit2Sharp.Repository(_tempDirectory);
        LibGit2Sharp.Commands.Stage(repo, "*");
        var sig = new LibGit2Sharp.Signature("Test", "test@test", DateTimeOffset.UtcNow);
        repo.Commit(message, sig, sig, new LibGit2Sharp.CommitOptions());
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
        catch { }
    }
}