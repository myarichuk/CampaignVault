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

public sealed class VaultSyncClassificationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();
    private readonly CampaignSync.CampaignSyncClient _mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();

    public VaultSyncClassificationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "cv_classify_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _session.Dispose();
        TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public async Task GetEntitySyncPlans_LocalOnly_WhenEntityExistsOnlyOnDisk()
    {
        await CreateVaultAsync();
        await WriteEntityOnDiskAsync(GrogMarkdown("Local only notes."));

        SetupEmptyRemote();
        await _session.FetchAsync();

        var item = FindPlan("characters/grog");
        Assert.Equal(VaultSyncState.LocalOnly, item.State);
    }

    [Fact]
    public async Task GetEntitySyncPlans_RemoteOnly_WhenEntityExistsOnlyInCache()
    {
        await CreateVaultAsync();
        SetupRemoteEntities(("characters/grog", "character", GrogJson("Remote only.")));
        await _session.FetchAsync();

        var item = FindPlan("characters/grog");
        Assert.Equal(VaultSyncState.RemoteOnly, item.State);
    }

    [Fact]
    public async Task GetEntitySyncPlans_DeletedLocally_WhenCommittedDeleteAndRemoteExists()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("To be deleted."));
        await AdvanceSyncedToHeadAsync();
        SetupRemoteEntities(("characters/grog", "character", GrogJson("To be deleted.")));
        await _session.FetchAsync();

        File.Delete(Path.Combine(_tempDirectory, "characters", "grog.md"));
        await CommitAllAsync("delete grog");

        var item = FindPlan("characters/grog");
        Assert.Equal(VaultSyncState.DeletedLocally, item.State);
    }

    [Fact]
    public async Task GetEntitySyncPlans_DeletedRemotely_WhenLocalExistsButRemoteAbsent()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Local copy."));
        await AdvanceSyncedToHeadAsync();
        SetupEmptyRemote();
        await _session.FetchAsync();

        var item = FindPlan("characters/grog");
        Assert.Equal(VaultSyncState.DeletedRemotely, item.State);
    }

    [Fact]
    public async Task GetEntitySyncPlans_DirtyWorkingTree_ReflectsWorkingTreeContent()
    {
        await CreateVaultAsync();
        await WriteAndCommitEntityAsync(GrogMarkdown("Committed baseline."));
        await AdvanceSyncedToHeadAsync();
        SetupRemoteEntities(("characters/grog", "character", GrogJson("Committed baseline.")));
        await _session.FetchAsync();

        await WriteEntityOnDiskAsync(GrogMarkdown("Dirty working tree edit."));

        var item = FindPlan("characters/grog");
        Assert.Equal(VaultSyncState.AheadOfVault, item.State);
    }

    [Fact]
    public async Task FetchAsync_WhenSyncNotConfigured_Throws()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");
        var ex = await Assert.ThrowsAsync<VaultException>(() => _session.FetchAsync());
        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSyncSummary_WhenManifestCorrupt_FlagsCorruptCache()
    {
        await CreateVaultAsync();
        var manifestPath = Path.Combine(
            _tempDirectory,
            VaultPaths.AppConfigDirectoryName,
            "remote-cache",
            VaultRemoteCache.ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, "{ not valid json");

        var summary = _session.GetSyncSummary();
        Assert.True(summary.RemoteCacheCorrupt);
        Assert.Equal(VaultConnectionState.Error, summary.Connection.State);
    }

    [Fact]
    public async Task GetEntitySyncPlans_Invalid_WhenEntityFailsCanonicalParse()
    {
        await CreateVaultAsync();
        await WriteEntityOnDiskAsync("""
            ---
            id: characters/broken
            name: Broken
            currentHp: not-a-number
            maxHp: 20
            ---
            Notes
            """, "characters/broken.md");

        var item = FindPlan("characters/broken");
        Assert.Equal(VaultSyncState.Invalid, item.State);
        Assert.False(string.IsNullOrWhiteSpace(item.ParseError));
    }

    private VaultEntitySyncPlan FindPlan(string entityId) =>
        _session.GetEntitySyncPlans().Single(p =>
            string.Equals(p.EntityId, entityId, StringComparison.OrdinalIgnoreCase));

    private async Task CreateVaultAsync()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e");
        _session.ConfigureVaultSync(() => _mockClient);
    }

    private void SetupEmptyRemote() => SetupRemoteEntities();

    private void SetupRemoteEntities(params (string Id, string Type, string Json)[] entities)
    {
        var response = new EntityListResponse();
        foreach (var (id, type, json) in entities)
        {
            response.Entities.Add(new EntityItem { Id = id, Type = type, Content = json });
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
        new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    private static string GrogJson(string notes) => $$"""
        {
          "id": "characters/grog",
          "name": "Grog",
          "currentHp": 10,
          "maxHp": 20,
          "notes": "{{notes}}"
        }
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

    private async Task WriteEntityOnDiskAsync(string markdown, string relativePath = "characters/grog.md")
    {
        var path = Path.Combine(_tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, markdown);
        await ReopenSessionAsync();
    }

    private async Task WriteAndCommitEntityAsync(string markdown, string relativePath = "characters/grog.md")
    {
        await WriteEntityOnDiskAsync(markdown, relativePath);
        await CommitAllAsync("add entity");
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