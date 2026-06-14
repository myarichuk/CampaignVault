using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceViewModelTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly WorkspaceViewModel _workspace;
    private readonly CampaignStateService _stateService;

    public WorkspaceViewModelTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "WorkspaceVM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _workspace = new WorkspaceViewModel();
        _stateService = new CampaignStateService(_workspace.DbService);
        _workspace.SetStateService(_stateService);
        _workspace.CurrentDirectory = _tempDirectory;
        _workspace.DbService.InitializeDatabase(_tempDirectory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, true);
        }
        catch { }
    }

    [Fact]
    public void RefreshFilesList_GroupsEntitiesByTopLevelFolder()
    {
        _stateService.Entities.Add(new UnifiedEntity
        {
            Id = "characters/grog",
            Name = "Grog",
            EntityType = "character",
            RelativePath = "characters/grog.md",
            LocalHash = "abc"
        });
        _stateService.Entities.Add(new UnifiedEntity
        {
            Id = "locations/tavern",
            Name = "Tavern",
            EntityType = "location",
            RelativePath = "locations/tavern.md",
            LocalHash = "def"
        });

        _workspace.RefreshFilesList();

        Assert.Equal(2, _workspace.Categories.Count);
        Assert.Contains(_workspace.Categories, c => c.Title == "characters");
        Assert.Contains(_workspace.Categories, c => c.Title == "locations");

        var characters = _workspace.Categories.First(c => c.Title == "characters");
        var grog = Assert.Single(characters.Children.OfType<EntityNodeViewModel>());
        Assert.Equal("Grog", grog.Title);
    }

    [Fact]
    public void RefreshFilesList_ReflectsNestedSubfolders()
    {
        _stateService.Entities.Add(new UnifiedEntity
        {
            Id = "characters/npcs/grog",
            Name = "Grog",
            EntityType = "character",
            RelativePath = "characters/npcs/grog.md",
            LocalHash = "abc"
        });
        _stateService.Entities.Add(new UnifiedEntity
        {
            Id = "characters/npcs/kael",
            Name = "Kael",
            EntityType = "character",
            RelativePath = "characters/npcs/kael.md",
            LocalHash = "def"
        });

        _workspace.RefreshFilesList();

        var characters = Assert.Single(_workspace.Categories);
        Assert.Equal("characters", characters.Title);

        var npcs = Assert.Single(characters.Children.OfType<FolderNodeViewModel>());
        Assert.Equal("npcs", npcs.Title);
        Assert.Equal(2, npcs.Children.OfType<EntityNodeViewModel>().Count());
    }

    [Fact]
    public async Task RefreshFilesList_PreservesEntityReferencesAfterStateRefresh()
    {
        var charsDir = Path.Combine(_tempDirectory, "characters");
        Directory.CreateDirectory(charsDir);
        await File.WriteAllTextAsync(Path.Combine(charsDir, "grog.md"), @"---
$type: ""character""
id: ""characters/grog""
name: ""Grog""
---
Notes");

        var scanner = new WorkspaceScanner(_workspace.DbService, _workspace.Parser);
        await scanner.ScanWorkspaceAsync(_tempDirectory);
        await _stateService.RefreshStateAsync("test-campaign");
        _workspace.RefreshFilesList();

        var firstNode = _workspace.Categories
            .SelectMany(c => c.Children.OfType<EntityNodeViewModel>())
            .Single();
        var firstEntity = firstNode.Entity;

        await _stateService.RefreshStateAsync("test-campaign");
        _workspace.RefreshFilesList();

        var secondNode = _workspace.Categories
            .SelectMany(c => c.Children.OfType<EntityNodeViewModel>())
            .Single();

        Assert.Same(firstEntity, secondNode.Entity);
    }
}