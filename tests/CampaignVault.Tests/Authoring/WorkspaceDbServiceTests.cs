using System;
using System.IO;
using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class WorkspaceDbServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly WorkspaceDbService _dbService;

    public WorkspaceDbServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        _dbService = new WorkspaceDbService();
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
    public void InitializeDatabase_CreatesDirectoryAndDatabaseFile()
    {
        _dbService.InitializeDatabase(_tempDirectory);

        var dbPath = Path.Combine(_tempDirectory, ".cv", "index.db");
        Assert.True(Directory.Exists(Path.Combine(_tempDirectory, ".cv")));
        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void UpsertAndGetEntity_SavesAndRetrievesRecordCorrectly()
    {
        _dbService.InitializeDatabase(_tempDirectory);

        var id = "characters/grog";
        var type = "character";
        var path = "characters/grog.md";
        var hash = "abc123hash";
        var lastSyncedHash = "abc123hash";
        var status = "Synced";
        var schema = "{\"name\":\"Grog\"}";

        _dbService.UpsertEntity(id, type, path, hash, lastSyncedHash, status, schema);

        var record = _dbService.GetEntity(id);

        Assert.NotNull(record);
        Assert.Equal(id, record.Id);
        Assert.Equal(type, record.EntityType);
        Assert.Equal(path, record.RelativePath);
        Assert.Equal(hash, record.FileHash);
        Assert.Equal(lastSyncedHash, record.LastSyncedHash);
        Assert.Equal(status, record.SyncStatus);
        Assert.Equal(schema, record.SchemaData);
    }

    [Fact]
    public void DeleteEntity_RemovesRecordSuccessfully()
    {
        _dbService.InitializeDatabase(_tempDirectory);

        var id = "characters/grog";
        _dbService.UpsertEntity(id, "character", "grog.md", "hash", null, "AddedLocally", "{}");

        var recordBefore = _dbService.GetEntity(id);
        Assert.NotNull(recordBefore);

        _dbService.DeleteEntity(id);

        var recordAfter = _dbService.GetEntity(id);
        Assert.Null(recordAfter);
    }
}
