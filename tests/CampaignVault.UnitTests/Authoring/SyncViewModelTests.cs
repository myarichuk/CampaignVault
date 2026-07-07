using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Git;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Grpc;
using Grpc.Core;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public sealed class SyncViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();
    private readonly CampaignSync.CampaignSyncClient _mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();

    public SyncViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "cv_syncvm_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _session.Dispose();
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ResolveMergedCommand_WritesEditedMergedContentAndClearsConflict()
    {
        var settings = new SettingsViewModel();
        var syncViewModel = new SyncViewModel(settings);
        syncViewModel.Bind(_session);

        await _session.CreateAsync(_tempDirectory, "test-campaign", "Dnd5e");
        _session.ConfigureVaultSync(() => _mockClient);

        await WriteAndCommitAsync(GrogMarkdown("Base."));
        await AdvanceSyncedToHeadAsync();
        SetupFetchMock(GrogJson("Base."));
        await _session.FetchAsync();

        await WriteAndCommitAsync(GrogMarkdown("Local edit."));
        SetupFetchMock(GrogJson("Vault edit."));
        await _session.FetchAsync();

        await syncViewModel.RefreshPlansCommand.ExecuteAsync(null);
        syncViewModel.SelectedPlan = syncViewModel.SyncPlans[0];
        Assert.True(syncViewModel.IsConflictSelected);

        const string mergedText = "---\nid: characters/grog\nname: Grog\ncurrentHp: 10\nmaxHp: 20\n---\nMerged by hand.";
        syncViewModel.SelectedPlan.MergedContent = mergedText;

        await syncViewModel.ResolveMergedCommand.ExecuteAsync(null);

        var content = await File.ReadAllTextAsync(Path.Combine(_tempDirectory, "characters", "grog.md"));
        Assert.Contains("Merged by hand.", content);
    }

    private void SetupFetchMock(string remoteJson)
    {
        var fetchResponse = new EntityListResponse();
        fetchResponse.Entities.Add(new EntityItem { Id = "characters/grog", Type = "character", Content = remoteJson });

        var fetchCall = CreateFakeUnaryCall(fetchResponse);
        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fetchCall);
        _mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<CallOptions>())
            .Returns(fetchCall);
    }

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(TResponse response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

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

    private async Task WriteAndCommitAsync(string markdown)
    {
        var path = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, markdown);
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
}
