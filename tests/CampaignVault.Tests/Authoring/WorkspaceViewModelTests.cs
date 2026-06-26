using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Authoring.Vault;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CampaignVaultSession _session;
    private readonly WorkspaceViewModel _workspace;

    public WorkspaceViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "VaultWorkspaceVm_" + Guid.NewGuid().ToString("N"));
        _session = new CampaignVaultSession();
        _workspace = new WorkspaceViewModel();
    }

    public void Dispose()
    {
        _workspace.Dispose();
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
    public async Task BindSession_BuildsExplorerTree_FromVaultEntities()
    {
        await _session.CreateAsync(_tempDirectory, "test-campaign");

        var entityPath = Path.Combine(_tempDirectory, "characters", "grog.md");
        Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
        await File.WriteAllTextAsync(entityPath,
            "---\nid: characters/grog\nname: Grog\n---\n\nA barbarian.");

        _workspace.BindSession(_session);
        _workspace.RefreshFilesList();

        var entityNode = _workspace.Categories
            .SelectMany(c => c.Children)
            .OfType<EntityNodeViewModel>()
            .FirstOrDefault(n => n.Entity.Id == "characters/grog");

        Assert.NotNull(entityNode);
        Assert.Equal("Grog", entityNode.Title);
    }
}