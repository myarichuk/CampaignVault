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
}