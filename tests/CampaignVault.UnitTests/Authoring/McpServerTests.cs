using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Tools;
using CampaignVault.Authoring.Vault;
using CampaignVault.Grpc;
using Grpc.Core;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests.Authoring;

[Collection("WorkspaceService")]
public class McpServerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session = new();

    public McpServerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "mcp_session_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        AuthoringMcpSessionHelper.ResetWorkspaceStateProvider();
        _session.Dispose();
        TryDeleteDirectory(_tempDirectory);
    }

    [Fact]
    public async Task ListWorkspaceEntities_NoVault_ReturnsError()
    {
        var mockWorkspace = Substitute.For<IWorkspaceState>();
        mockWorkspace.Session.Returns((CampaignVaultSession?)null);
        AuthoringMcpSessionHelper.WorkspaceStateProvider = () => mockWorkspace;
        var tools = new AuthoringMcpTools();

        var result = await tools.ListWorkspaceEntities();

        Assert.False(result.success);
        Assert.Equal(AuthoringMcpSessionHelper.NoVaultError, result.error);
    }

    [Fact]
    public async Task WriteWorkspaceEntity_InvalidYaml_ReturnsError()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var result = await tools.WriteWorkspaceEntity(
            "characters/bad.md",
            "---\nid: characters/bad\n  badIndent: true\n---\n# Test");

        Assert.False(result.success);
        Assert.Contains("YAML", result.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteThenList_ShowsEntityInScanResults()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var writeResult = await tools.WriteWorkspaceEntity(
            "characters/test.md",
            "---\nid: characters/test\nname: Test\n---\n# Test");

        Assert.True(writeResult.success);

        var listResult = await tools.ListWorkspaceEntities();
        Assert.True(listResult.success);

        var entities = Assert.IsAssignableFrom<System.Collections.IEnumerable>(listResult.files)
            .Cast<object>()
            .ToList();
        Assert.Single(entities);
    }

    [Fact]
    public async Task ReadAndWriteWorkspaceEntity_RoundTripsThroughSession()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        const string testContent = "---\nid: characters/test\nname: Test\n---\n# Test";
        var writeResult = await tools.WriteWorkspaceEntity("characters/test", testContent);

        Assert.True(writeResult.success);
        Assert.Equal("characters/test.md", writeResult.path);

        var readResult = await tools.ReadWorkspaceEntity("characters/test.md");
        Assert.True(readResult.success);
        Assert.Equal(testContent, readResult.content);
    }

    [Fact]
    public async Task DeleteWorkspaceEntity_RemovesFileThroughLockGuardedSession()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        const string testContent = "---\nid: characters/test\nname: Test\n---\n# Test";
        await tools.WriteWorkspaceEntity("characters/test", testContent);

        var deleteResult = await tools.DeleteWorkspaceEntity("characters/test.md");

        Assert.True(deleteResult.success, deleteResult.error);
        Assert.False(File.Exists(Path.Combine(_tempDirectory, "characters", "test.md")));
    }

    [Fact]
    public async Task FetchVault_WithMockClient_WritesRemoteCache()
    {
        await OpenVaultAsync();
        var mockClient = Substitute.For<CampaignSync.CampaignSyncClient>();
        var response = new EntityListResponse();
        var call = CreateFakeUnaryCall(response);
        mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<Metadata>(),
                Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call);
        mockClient
            .GetCampaignEntitiesAsync(Arg.Any<GetCampaignEntitiesRequest>(), Arg.Any<CallOptions>())
            .Returns(call);

        _session.ConfigureVaultSync(() => mockClient);

        var tools = new AuthoringMcpTools();
        var result = await tools.FetchVault();

        Assert.True(result.success, result.error);
        Assert.NotNull(result.summary);

        var cacheDir = Path.Combine(_tempDirectory, ".cv", "remote-cache");
        Assert.True(Directory.Exists(cacheDir));
        Assert.True(File.Exists(Path.Combine(cacheDir, "manifest.json")));
    }

    [Fact]
    public async Task PushToVault_NoVault_ReturnsError()
    {
        var mockWorkspace = Substitute.For<IWorkspaceState>();
        mockWorkspace.Session.Returns((CampaignVaultSession?)null);
        AuthoringMcpSessionHelper.WorkspaceStateProvider = () => mockWorkspace;
        var tools = new AuthoringMcpTools();

        var result = await tools.PushToVault();

        Assert.False(result.success);
        Assert.Equal(AuthoringMcpSessionHelper.NoVaultError, result.error);
    }

    [Fact]
    public async Task GetVaultStatus_NoVault_ReturnsError()
    {
        var mockWorkspace = Substitute.For<IWorkspaceState>();
        mockWorkspace.Session.Returns((CampaignVaultSession?)null);
        AuthoringMcpSessionHelper.WorkspaceStateProvider = () => mockWorkspace;
        var tools = new AuthoringMcpTools();

        var result = await tools.GetVaultStatus();

        Assert.False(result.success);
        Assert.Equal(AuthoringMcpSessionHelper.NoVaultError, result.error);
    }

    [Fact]
    public async Task GetVaultStatus_OpenVault_ReturnsStatusPayload()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        // Write a file to make working tree dirty
        await tools.WriteWorkspaceEntity("characters/test.md", "---\nid: characters/test\nname: Test\n---");

        var result = await tools.GetVaultStatus();

        Assert.True(result.success, result.error);
        Assert.NotNull(result.summary);

        var payload = Assert.IsType<System.Collections.Generic.Dictionary<string, object>>(result.summary);
        Assert.True((bool)payload["isDirty"]);
        Assert.NotNull(payload["vaultPath"]);
        Assert.NotNull(payload["sync"]);
    }

    [Fact]
    public async Task CommitVault_WritesCommit_HeadShaAdvances()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        // Make a change
        await tools.WriteWorkspaceEntity("characters/test.md", "---\nid: characters/test\nname: Test\n---");

        var statusBefore = await tools.GetVaultStatus();
        var headBefore = ((System.Collections.Generic.Dictionary<string, object>)statusBefore.summary!)["headCommitSha"];

        // Commit
        var commitResult = await tools.CommitVault("Test commit");
        Assert.True(commitResult.success);
        var headAfter = ((System.Collections.Generic.Dictionary<string, object>)commitResult.summary!)["headCommitSha"];

        Assert.NotEqual(headBefore, headAfter);

        // Verify working tree is clean
        var statusAfter = await tools.GetVaultStatus();
        var isDirty = ((System.Collections.Generic.Dictionary<string, object>)statusAfter.summary!)["isDirty"];
        Assert.False((bool)isDirty);
    }

    [Fact]
    public async Task CommitVault_EmptyMessage_ReturnsError()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var result = await tools.CommitVault("");

        Assert.False(result.success);
        Assert.Contains("required", result.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateWorkspaceEntity_ValidType_WritesTemplateAndMatchesUiPath()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var result = await tools.CreateWorkspaceEntity("character", "TestNPC");

        Assert.True(result.success, result.error);
        Assert.NotNull(result.path);
        Assert.NotNull(result.content);
        Assert.StartsWith("characters/", result.path);
        Assert.EndsWith(".md", result.path);
        Assert.Contains("testnpc", result.path);

        // Verify file was written
        var readResult = await tools.ReadWorkspaceEntity(result.path);
        Assert.True(readResult.success);
        Assert.Equal(result.content, readResult.content);
    }

    [Fact]
    public async Task CreateWorkspaceEntity_UnsupportedType_ReturnsError()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var result = await tools.CreateWorkspaceEntity("invalid-type", "Test");

        Assert.False(result.success);
        Assert.Contains("Unsupported", result.error);
    }

    [Fact]
    public async Task ResolveVaultConflict_UnknownResolutionString_ReturnsError()
    {
        await OpenVaultAsync();
        var tools = new AuthoringMcpTools();

        var result = await tools.ResolveVaultConflict("characters/test", "InvalidResolution");

        Assert.False(result.success);
        Assert.Contains("Unknown resolution", result.error);
    }

    private async Task OpenVaultAsync()
    {
        await _session.CreateAsync(_tempDirectory, "mcp-test");
        var mockWorkspace = Substitute.For<IWorkspaceState>();
        mockWorkspace.Session.Returns(_session);
        AuthoringMcpSessionHelper.WorkspaceStateProvider = () => mockWorkspace;
    }

    private static AsyncUnaryCall<TResponse> CreateFakeUnaryCall<TResponse>(TResponse response) =>
        new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}