using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Tools;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

[Collection("WorkspaceService")]
public class McpServerTests
{
    [Fact]
    public async Task ListWorkspaceEntities_NoWorkspace_ReturnsError()
    {
        WorkspaceService.MainWindowViewModel = null;
        var tools = new AuthoringMcpTools();

        var result = await tools.ListWorkspaceEntities();
        Assert.NotNull(result);

        Assert.False(result.success);
        Assert.Equal("No workspace directory loaded.", result.error);
    }

    [Fact]
    public async Task ReadAndWriteWorkspaceEntity_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var mainVm = new MainWindowViewModel();
            mainVm.Workspace.LoadDirectory(tempDir);
            WorkspaceService.MainWindowViewModel = mainVm;

            var tools = new AuthoringMcpTools();

            // Write
            var testFilePath = Path.Combine(tempDir, "test.md");
            var testContent = "---\n$type: character\n---\n# Test";
            var writeResult = await tools.WriteWorkspaceEntity(testFilePath, testContent);

            Assert.True(writeResult.success);
            Assert.True(File.Exists(testFilePath));

            // Read
            var readResult = await tools.ReadWorkspaceEntity(testFilePath);

            Assert.True(readResult.success);
            Assert.Equal(testContent, readResult.content);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
