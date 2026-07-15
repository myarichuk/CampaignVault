using System;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CampaignToolBaseTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public CampaignToolBaseTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class ProbeTool(CampaignRepository repository, CampaignDocumentKeys keys)
        : CampaignToolBase(repository, keys)
    {
        public Task<ToolResult<string>> RunThrowingAction() =>
            ExecuteAsync<string>(_ => throw new InvalidOperationException("sensitive internal detail: connection string=secret"));
    }

    [Fact]
    public async Task ExecuteAsync_UnhandledException_DoesNotLeakExceptionMessageToCaller()
    {
        var repo = _fixture.CreateRepository();
        var tool = new ProbeTool(repo, new CampaignDocumentKeys());

        var result = await tool.RunThrowingAction();

        Assert.False(result.Success);
        Assert.Equal(ToolErrors.InternalError, result.Error);
        Assert.DoesNotContain("sensitive internal detail", result.Summary);
        Assert.DoesNotContain("connection string", result.Summary);
    }
}
