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