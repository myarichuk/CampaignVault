using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class MainWindowViewModelTests
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindowViewModelTests()
    {
        _viewModel = new MainWindowViewModel();
    }

    [Fact]
    public void HasSelection_ReturnsTrue_WhenEntityIsSelected()
    {
        Assert.False(_viewModel.HasSelection);

        _viewModel.Workspace.SelectedNode = new EntityNodeViewModel(new UnifiedEntity { Name = "Test" });

        Assert.True(_viewModel.HasSelection);
    }

    [Fact]
    public void HasParseError_ReturnsTrue_WhenParsedCharacterIsNullAndEditorTextIsNotEmpty()
    {
        _viewModel.Workspace.SelectedNode = new EntityNodeViewModel(new UnifiedEntity { Name = "Test" });
        _viewModel.EditorText = "Invalid YAML content";
        
        // OnEditorTextChanged should have been triggered, resulting in a parse error if YAML is invalid.
        // Since we didn't mock the parser, it will likely fail on "Invalid YAML content" if it expects frontmatter.
        
        Assert.Null(_viewModel.ParsedCharacter);
        Assert.True(_viewModel.HasParseError);
    }

    [Fact]
    public void HasParseError_ReturnsFalse_WhenEditorTextIsEmpty()
    {
        _viewModel.Workspace.SelectedNode = new EntityNodeViewModel(new UnifiedEntity { Name = "Test" });
        _viewModel.EditorText = string.Empty;

        Assert.False(_viewModel.HasParseError);
    }
}
