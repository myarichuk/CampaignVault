using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Grpc;
using CampaignVault.Models;
using Grpc.Core;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class SyncViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly SettingsViewModel _settings;
    private readonly WorkspaceViewModel _workspace;
    private readonly CampaignStateService _campaignState;
    private readonly SyncViewModel _syncViewModel;
    private readonly CampaignSync.CampaignSyncClient _mockClient;

    public SyncViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "TestCampaign_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _settings = new SettingsViewModel();
        _workspace = new WorkspaceViewModel();
        _campaignState = new CampaignStateService(_workspace.DbService);

        // Manual quiet load to avoid background threads and dispatcher calls
        _workspace.CurrentDirectory = _tempDirectory;
        _workspace.DbService.InitializeDatabase(_tempDirectory);

        _syncViewModel = new SyncViewModel(_settings, _workspace, _campaignState);

        _mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();
        _syncViewModel.ClientFactory = () => _mockClient;
        _campaignState.SetClientFactory(() => _mockClient);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task PopulateActualDiffs_LocalOnly_WorksCorrectly()
    {
        // 1. Arrange
        var charId = "characters/grog";
        var relativePath = "characters/grog.md";
        var absolutePath = Path.Combine(_tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var localMarkdown =
            "---\n$type: character\nid: characters/grog\nname: Grog\ncampaignName: TestCampaign\n---\nNotes about Grog";
        await File.WriteAllTextAsync(absolutePath, localMarkdown);

        _workspace.DbService.UpsertEntity(
            charId,
            "character",
            relativePath,
            _syncViewModel.CallPrivateComputeHash(localMarkdown),
            null, // Not synced yet
            "LocalOnly",
            "{}"
        );

        var remoteResponse = new EntityListResponse(); // Empty remote
        var fakeCall = CreateFakeUnaryCall(remoteResponse);
        _mockClient.GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakeCall);

        var pushResponse = new PushResponse { Success = true, Message = "Pushed" };
        var fakePushCall = CreateFakeUnaryCall(pushResponse);
        _mockClient.PushCampaignEntityAsync(Arg.Any<PushCampaignEntityRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakePushCall);

        // 2. Act - Scan diffs
        await _syncViewModel.PopulateActualDiffsAsync();

        // 3. Assert Diff
        Assert.Single(_syncViewModel.SyncDiffs);
        var diff = _syncViewModel.SyncDiffs[0];
        Assert.Equal("LocalOnly", diff.Status);
        Assert.Equal(charId, diff.EntityId);
        Assert.Equal(localMarkdown, diff.LocalContent);

        // 4. Act - Push
        _syncViewModel.SelectedDiff = diff;
        await _syncViewModel.PushSelectedCommand.ExecuteAsync(null);

        // 5. Assert database updated
        var dbRecord = _workspace.DbService.GetEntity(charId);
        Assert.NotNull(dbRecord);
        Assert.Equal("Synced", dbRecord.SyncStatus);
        Assert.Equal(_syncViewModel.CallPrivateComputeHash(localMarkdown), dbRecord.LastSyncedHash);
        Assert.Empty(_syncViewModel.SyncDiffs);
    }

    [Fact]
    public async Task PopulateActualDiffs_RemoteOnly_WorksCorrectly()
    {
        // 1. Arrange
        var charId = "characters/grog";
        var character = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Remote Grog notes"
        };
        var jsonContent = JsonSerializer.Serialize(character);

        var remoteResponse = new EntityListResponse();
        remoteResponse.Entities.Add(new EntityItem
        {
            Id = "characters/grog",
            Type = "character",
            Content = jsonContent
        });

        var fakeCall = CreateFakeUnaryCall(remoteResponse);
        _mockClient.GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakeCall);

        // 2. Act - Scan diffs
        await _syncViewModel.PopulateActualDiffsAsync();

        // 3. Assert Diff
        Assert.Single(_syncViewModel.SyncDiffs);
        var diff = _syncViewModel.SyncDiffs[0];
        Assert.Equal("RemoteOnly", diff.Status);
        Assert.Equal(charId, diff.EntityId);
        Assert.Contains("Remote Grog notes", diff.RemoteContent);

        // 4. Act - Pull
        _syncViewModel.SelectedDiff = diff;
        await _syncViewModel.PullSelectedCommand.ExecuteAsync(null);

        // 5. Assert File and Database written
        var expectedPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        Assert.True(File.Exists(expectedPath));
        var contentOnDisk = await File.ReadAllTextAsync(expectedPath);
        Assert.Contains("Remote Grog notes", contentOnDisk);

        var dbRecord = _workspace.DbService.GetEntity(charId);
        Assert.NotNull(dbRecord);
        Assert.Equal("Synced", dbRecord.SyncStatus);
        Assert.Equal(_syncViewModel.CallPrivateComputeHash(contentOnDisk), dbRecord.LastSyncedHash);
        Assert.Empty(_syncViewModel.SyncDiffs);
    }

    [Fact]
    public async Task PopulateActualDiffs_ModifiedLocally_WorksCorrectly()
    {
        // 1. Arrange
        var charId = "characters/grog";
        var relativePath = "characters/grog.md";
        var absolutePath = Path.Combine(_tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var character = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Synced Grog"
        };
        var jsonContent = JsonSerializer.Serialize(character);
        var remoteItem = new EntityItem { Id = "characters/grog", Type = "character", Content = jsonContent };
        var syncedMarkdown = _syncViewModel.CallPrivateDeserializeRemoteToMarkdown(remoteItem);
        var syncedHash = _syncViewModel.CallPrivateComputeHash(syncedMarkdown);

        var modifiedMarkdown = syncedMarkdown.Replace("Synced Grog", "Modified Grog");
        await File.WriteAllTextAsync(absolutePath, modifiedMarkdown);

        _workspace.DbService.UpsertEntity(
            charId,
            "character",
            relativePath,
            _syncViewModel.CallPrivateComputeHash(modifiedMarkdown),
            syncedHash, // Synced hash matches the remote
            "ModifiedLocally",
            "{}"
        );

        var remoteResponse = new EntityListResponse();
        remoteResponse.Entities.Add(remoteItem);

        var fakeCall = CreateFakeUnaryCall(remoteResponse);
        _mockClient.GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakeCall);

        var debugRecord = _workspace.DbService.GetEntity(charId);
        Assert.NotNull(debugRecord);
        Assert.Equal(syncedHash, debugRecord.LastSyncedHash);
        Assert.Equal(_syncViewModel.CallPrivateComputeHash(modifiedMarkdown), debugRecord.FileHash);

        // 2. Act - Scan diffs
        await _syncViewModel.PopulateActualDiffsAsync();

        // 3. Assert Diff
        Assert.Single(_syncViewModel.SyncDiffs);
        var diff = _syncViewModel.SyncDiffs[0];
        Assert.Equal("ModifiedLocally", diff.Status);
    }

    [Fact]
    public async Task PopulateActualDiffs_ModifiedRemotely_WorksCorrectly()
    {
        // 1. Arrange
        var charId = "characters/grog";
        var relativePath = "characters/grog.md";
        var absolutePath = Path.Combine(_tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var characterSynced = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Synced Grog"
        };
        var syncedJson = JsonSerializer.Serialize(characterSynced);
        var remoteSyncedItem = new EntityItem { Id = "characters/grog", Type = "character", Content = syncedJson };
        var syncedMarkdown = _syncViewModel.CallPrivateDeserializeRemoteToMarkdown(remoteSyncedItem);
        var syncedHash = _syncViewModel.CallPrivateComputeHash(syncedMarkdown);

        await File.WriteAllTextAsync(absolutePath, syncedMarkdown); // Local remains unchanged at synced version

        _workspace.DbService.UpsertEntity(
            charId,
            "character",
            relativePath,
            syncedHash,
            syncedHash,
            "Synced",
            "{}"
        );

        var characterModified = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Modified Remote Grog" // Remote is modified
        };
        var modifiedJson = JsonSerializer.Serialize(characterModified);
        var remoteModifiedItem = new EntityItem { Id = "characters/grog", Type = "character", Content = modifiedJson };

        var remoteResponse = new EntityListResponse();
        remoteResponse.Entities.Add(remoteModifiedItem);

        var fakeCall = CreateFakeUnaryCall(remoteResponse);
        _mockClient.GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakeCall);

        // 2. Act - Scan diffs
        await _syncViewModel.PopulateActualDiffsAsync();

        // 3. Assert Diff
        Assert.Single(_syncViewModel.SyncDiffs);
        var diff = _syncViewModel.SyncDiffs[0];
        Assert.Equal("ModifiedRemotely", diff.Status);
    }

    [Fact]
    public async Task PopulateActualDiffs_Conflict_WorksCorrectly()
    {
        // 1. Arrange
        var charId = "characters/grog";
        var relativePath = "characters/grog.md";
        var absolutePath = Path.Combine(_tempDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var characterSynced = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Synced Grog"
        };
        var syncedJson = JsonSerializer.Serialize(characterSynced);
        var remoteSyncedItem = new EntityItem { Id = "characters/grog", Type = "character", Content = syncedJson };
        var syncedMarkdown = _syncViewModel.CallPrivateDeserializeRemoteToMarkdown(remoteSyncedItem);
        var syncedHash = _syncViewModel.CallPrivateComputeHash(syncedMarkdown);

        var localModifiedMarkdown = syncedMarkdown.Replace("Synced Grog", "Local Modified Grog");
        await File.WriteAllTextAsync(absolutePath, localModifiedMarkdown);

        _workspace.DbService.UpsertEntity(
            charId,
            "character",
            relativePath,
            _syncViewModel.CallPrivateComputeHash(localModifiedMarkdown),
            syncedHash,
            "ModifiedLocally",
            "{}"
        );

        var characterRemoteModified = new Character
        {
            Id = "characters/grog",
            Name = "Grog",
            CampaignName = "TestCampaign",
            Notes = "Remote Modified Grog"
        };
        var remoteModifiedJson = JsonSerializer.Serialize(characterRemoteModified);
        var remoteModifiedItem = new EntityItem { Id = "characters/grog", Type = "character", Content = remoteModifiedJson };

        var remoteResponse = new EntityListResponse();
        remoteResponse.Entities.Add(remoteModifiedItem);

        var fakeCall = CreateFakeUnaryCall(remoteResponse);
        _mockClient.GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakeCall);

        var pushResponse = new PushResponse { Success = true, Message = "Pushed" };
        var fakePushCall = CreateFakeUnaryCall(pushResponse);
        _mockClient.PushCampaignEntityAsync(Arg.Any<PushCampaignEntityRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(fakePushCall);

        // 2. Act - Scan diffs
        await _syncViewModel.PopulateActualDiffsAsync();

        // 3. Assert Diff
        Assert.Single(_syncViewModel.SyncDiffs);
        var diff = _syncViewModel.SyncDiffs[0];
        Assert.Equal("Conflict", diff.Status);

        // 4. Act - Resolve Keep Local (Push)
        _syncViewModel.SelectedDiff = diff;
        await _syncViewModel.ResolveKeepLocalCommand.ExecuteAsync(null);

        // 5. Assert SQLite Updated
        var dbRecord = _workspace.DbService.GetEntity(charId);
        Assert.NotNull(dbRecord);
        Assert.Equal("Synced", dbRecord.SyncStatus);
        Assert.Equal(_syncViewModel.CallPrivateComputeHash(localModifiedMarkdown), dbRecord.LastSyncedHash);
        Assert.Empty(_syncViewModel.SyncDiffs);
    }

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(TResponse response)
    {
        return new AsyncUnaryCall<TResponse>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }
        );
    }
}

// Extension to allow invoking private helper functions of SyncViewModel in tests
public static class SyncViewModelTestExtensions
{
    public static string CallPrivateComputeHash(this SyncViewModel syncVm, string text)
    {
        var method = typeof(SyncViewModel).GetMethod("ComputeSha256Hash",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method == null) throw new InvalidOperationException("Could not find ComputeSha256Hash method");
        return (string)method.Invoke(syncVm, new object[] { text })!;
    }

    public static string CallPrivateDeserializeRemoteToMarkdown(this SyncViewModel syncVm, EntityItem remote)
    {
        // Now in CampaignStateService
        var stateService = (CampaignStateService)typeof(SyncViewModel).GetField("_campaignState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(syncVm)!;
        var method = typeof(CampaignStateService).GetMethod("DeserializeRemoteToMarkdown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method == null) throw new InvalidOperationException("Could not find DeserializeRemoteToMarkdown method");
        return (string)method.Invoke(stateService, new object[] { remote })!;
    }
}
