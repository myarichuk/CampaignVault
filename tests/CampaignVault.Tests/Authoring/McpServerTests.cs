using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Tools;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

[Collection("McpServerTests")]
public class McpServerTests
{
    [Fact]
    public async Task ListWorkspaceEntities_NoWorkspace_ReturnsError()
    {
        WorkspaceService.MainWindowViewModel = null;
        var tools = new AuthoringMcpTools();

        var result = await tools.ListWorkspaceEntities();
        Assert.NotNull(result);

        // Access via reflection or cast to dynamic
        var successProp = result.GetType().GetProperty("success")?.GetValue(result);
        var errorProp = result.GetType().GetProperty("error")?.GetValue(result);

        Assert.Equal(false, successProp);
        Assert.Equal("No workspace directory loaded.", errorProp);
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

            var writeSuccess = writeResult.GetType().GetProperty("success")?.GetValue(writeResult);
            Assert.Equal(true, writeSuccess);
            Assert.True(File.Exists(testFilePath));

            // Read
            var readResult = await tools.ReadWorkspaceEntity(testFilePath);
            var readSuccess = readResult.GetType().GetProperty("success")?.GetValue(readResult);
            var readContent = readResult.GetType().GetProperty("content")?.GetValue(readResult);

            Assert.Equal(true, readSuccess);
            Assert.Equal(testContent, readContent);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
