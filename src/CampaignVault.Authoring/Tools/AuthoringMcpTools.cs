using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using ModelContextProtocol.Server;
using CampaignVault.Authoring.Services;

namespace CampaignVault.Authoring.Tools;

[McpServerToolType]
public class AuthoringMcpTools
{
    [McpServerTool(UseStructuredContent = true)]
    [Description("Lists all campaign entities (npcs, locations, factions, quests) found in the active local workspace.")]
    public Task<object> ListWorkspaceEntities()
    {
        var mainVm = WorkspaceService.MainWindowViewModel;
        if (mainVm == null || string.IsNullOrEmpty(mainVm.Workspace.CurrentDirectory))
        {
            return Task.FromResult<object>(new { success = false, error = "No workspace directory loaded." });
        }

        var files = mainVm.Workspace.Files.Select(f => new {
            fileName = f.FileName,
            filePath = f.FilePath,
            relativeUrl = Path.GetRelativePath(mainVm.Workspace.CurrentDirectory, f.FilePath)
        }).ToList();

        return Task.FromResult<object>(new { success = true, files });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("Reads the YAML frontmatter and markdown body of a campaign entity file.")]
    public Task<object> ReadWorkspaceEntity(
        [Description("The absolute path or relative path to the campaign markdown file.")] string filePath)
    {
        var mainVm = WorkspaceService.MainWindowViewModel;
        var activeDir = mainVm?.Workspace.CurrentDirectory;

        var fullPath = filePath;
        if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(activeDir))
        {
            fullPath = Path.Combine(activeDir, filePath);
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<object>(new { success = false, error = $"File not found: {filePath}" });
        }

        try
        {
            var content = File.ReadAllText(fullPath);
            return Task.FromResult<object>(new { success = true, content });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("Writes or updates a campaign entity file with YAML frontmatter and markdown body content.")]
    public Task<object> WriteWorkspaceEntity(
        [Description("The absolute path or relative path to the campaign markdown file.")] string filePath,
        [Description("The full content of the file (including YAML frontmatter and markdown body).")] string content)
    {
        var mainVm = WorkspaceService.MainWindowViewModel;
        var activeDir = mainVm?.Workspace.CurrentDirectory;

        var fullPath = filePath;
        if (!Path.IsPathRooted(filePath) && !string.IsNullOrEmpty(activeDir))
        {
            fullPath = Path.Combine(activeDir, filePath);
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);

            // Refresh workspace list on UI thread
            if (mainVm != null && !string.IsNullOrEmpty(activeDir))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    mainVm.Workspace.LoadDirectory(activeDir);
                });
            }

            return Task.FromResult<object>(new { success = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("Triggers a campaign synchronization with the remote CampaignVault database via gRPC (Stub).")]
    public Task<object> TriggerVaultSync()
    {
        return Task.FromResult<object>(new { success = true, message = "Sync triggered successfully (stub)." });
    }
}
