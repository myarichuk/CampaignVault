using System;
using CampaignVault.Authoring.Tools;
using Xunit;

namespace CampaignVault.Tests.Authoring;

/// <summary>
/// Serializes MCP/vault tests that mutate <see cref="AuthoringMcpSessionHelper.WorkspaceStateProvider"/>.
/// </summary>
[CollectionDefinition("WorkspaceService")]
public sealed class WorkspaceServiceCollection : ICollectionFixture<WorkspaceServiceFixture>
{
}

public sealed class WorkspaceServiceFixture : IDisposable
{
    public WorkspaceServiceFixture()
    {
        AuthoringMcpSessionHelper.ResetWorkspaceStateProvider();
    }

    public void Dispose()
    {
        AuthoringMcpSessionHelper.ResetWorkspaceStateProvider();
    }
}