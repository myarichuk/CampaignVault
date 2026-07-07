using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class MainWindowViewModelTests
{
    [Fact]
    public void HasSelection_ReturnsTrue_WhenEntityIsSelected()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.HasSelection);

        viewModel.Workspace.SelectedNode = new EntityNodeViewModel(
            new VaultEntity
            {
                Id = "characters/test",
                EntityType = "character",
                RelativePath = "characters/test.md",
                ContentHash = "abc"
            },
            VaultSyncState.Synced,
            isGitDirty: false,
            hasLocalFile: true);

        Assert.True(viewModel.HasSelection);
    }

    [Fact]
    public void HasParseError_ReturnsTrue_WhenParsedCharacterIsNullAndEditorTextIsNotEmpty()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Workspace.SelectedNode = new EntityNodeViewModel(
            new VaultEntity
            {
                Id = "characters/test",
                EntityType = "character",
                RelativePath = "characters/test.md",
                ContentHash = "abc"
            },
            VaultSyncState.Synced,
            isGitDirty: false,
            hasLocalFile: true);
        viewModel.EditorText = "Invalid YAML content";

        Assert.Null(viewModel.ParsedCharacter);
        Assert.True(viewModel.HasParseError);
    }

    [Fact]
    public void HasParseError_ReturnsFalse_WhenEditorTextIsEmpty()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Workspace.SelectedNode = new EntityNodeViewModel(
            new VaultEntity
            {
                Id = "characters/test",
                EntityType = "character",
                RelativePath = "characters/test.md",
                ContentHash = "abc"
            },
            VaultSyncState.Synced,
            isGitDirty: false,
            hasLocalFile: true);
        viewModel.EditorText = string.Empty;

        Assert.False(viewModel.HasParseError);
    }

    [Fact]
    public async Task ReloadActiveFileContentAsync_SkipsReload_WhenEditorIsDirty()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MainWindowVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var viewModel = new MainWindowViewModel();
            await viewModel.Session.CreateAsync(tempDirectory, "test-campaign");

            var entityPath = Path.Combine(tempDirectory, "characters", "grog.md");
            Directory.CreateDirectory(Path.GetDirectoryName(entityPath)!);
            const string onDisk = "---\nid: characters/grog\nname: Grog\n---\nSaved.";
            await File.WriteAllTextAsync(entityPath, onDisk);

            var entity = new VaultEntity
            {
                Id = "characters/grog",
                EntityType = "character",
                RelativePath = "characters/grog.md",
                ContentHash = "abc"
            };
            viewModel.Workspace.SelectedNode = new EntityNodeViewModel(
                entity, VaultSyncState.Synced, isGitDirty: false, hasLocalFile: true);
            await Task.Delay(50); // allow the async SelectedNode handler to load EditorText

            const string unsavedEdit = "---\nid: characters/grog\nname: Grog\n---\nUnsaved edit.";
            viewModel.EditorText = unsavedEdit;
            Assert.True(viewModel.IsEditorDirty);

            await File.WriteAllTextAsync(entityPath, onDisk + " Changed on disk.");

            await viewModel.ReloadActiveFileContentAsync();

            Assert.Equal(unsavedEdit, viewModel.EditorText);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
            catch
            {
            }
        }
    }
}