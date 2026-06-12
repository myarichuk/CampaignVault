using System;
using System.IO;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceScannerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly WorkspaceDbService _dbService;
    private readonly WorkspaceParser _parser;
    private readonly WorkspaceScanner _scanner;

    public WorkspaceScannerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        
        _dbService = new WorkspaceDbService();
        _dbService.InitializeDatabase(_tempDirectory);
        _parser = new WorkspaceParser();
        _scanner = new WorkspaceScanner(_dbService, _parser);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch {}
    }

    [Fact]
    public async Task ScanWorkspaceAsync_ScansFoldersAndPopulatesDatabase()
    {
        // Setup characters folder and file
        var charsDir = Path.Combine(_tempDirectory, "characters");
        Directory.CreateDirectory(charsDir);
        var charFile = Path.Combine(charsDir, "grog.md");
        await File.WriteAllTextAsync(charFile, @"---
$type: ""character""
id: ""characters/grog""
name: ""Grog""
currentHp: 15
maxHp: 20
---
Grog notes body");

        // Setup locations folder and file
        var locsDir = Path.Combine(_tempDirectory, "locations");
        Directory.CreateDirectory(locsDir);
        var locFile = Path.Combine(locsDir, "tavern.md");
        await File.WriteAllTextAsync(locFile, @"---
$type: ""location""
id: ""locations/tavern""
name: ""Tavern""
---
Tavern description body");

        // Scan
        await _scanner.ScanWorkspaceAsync(_tempDirectory);

        // Verify Grog
        var grogRecord = _dbService.GetEntity("characters/grog");
        Assert.NotNull(grogRecord);
        Assert.Equal("character", grogRecord.EntityType);
        Assert.Equal("characters/grog.md", grogRecord.RelativePath);
        Assert.Equal("AddedLocally", grogRecord.SyncStatus);
        Assert.Contains("Grog", grogRecord.SchemaData);

        // Verify Tavern
        var tavernRecord = _dbService.GetEntity("locations/tavern");
        Assert.NotNull(tavernRecord);
        Assert.Equal("location", tavernRecord.EntityType);
        Assert.Equal("locations/tavern.md", tavernRecord.RelativePath);
        Assert.Equal("AddedLocally", tavernRecord.SyncStatus);
        Assert.Contains("Tavern", tavernRecord.SchemaData);
    }
}
