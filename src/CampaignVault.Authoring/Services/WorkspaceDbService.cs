using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace CampaignVault.Authoring.Services;

public class WorkspaceDbService
{
    private string _dbPath = string.Empty;

    public void InitializeDatabase(string workspacePath)
    {
        var cvDir = Path.Combine(workspacePath, ".cv");
        if (!Directory.Exists(cvDir))
        {
            var dirInfo = Directory.CreateDirectory(cvDir);
            // Hide the .cv directory on Windows
            if (OperatingSystem.IsWindows())
            {
                dirInfo.Attributes |= FileAttributes.Hidden;
            }
        }

        _dbPath = Path.Combine(cvDir, "index.db");

        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Entities (
                Id TEXT PRIMARY KEY,
                EntityType TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                FileHash TEXT NOT NULL,
                LastSyncedHash TEXT,
                SyncStatus TEXT NOT NULL,
                SchemaData TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Relationships (
                SourceId TEXT NOT NULL,
                TargetId TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                PRIMARY KEY (SourceId, TargetId, RelationType),
                FOREIGN KEY (SourceId) REFERENCES Entities(Id) ON DELETE CASCADE
            );
        ";
        command.ExecuteNonQuery();
    }

    public SqliteConnection GetConnection()
    {
        if (string.IsNullOrEmpty(_dbPath))
        {
            throw new InvalidOperationException("Database has not been initialized. Call InitializeDatabase first.");
        }
        return new SqliteConnection($"Data Source={_dbPath}");
    }

    public void UpsertEntity(
        string id, 
        string entityType, 
        string relativePath, 
        string fileHash, 
        string? lastSyncedHash, 
        string syncStatus, 
        string schemaData)
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Entities (Id, EntityType, RelativePath, FileHash, LastSyncedHash, SyncStatus, SchemaData)
            VALUES ($id, $type, $path, $hash, $syncedHash, $status, $schema)
            ON CONFLICT(Id) DO UPDATE SET
                EntityType = $type,
                RelativePath = $path,
                FileHash = $hash,
                LastSyncedHash = COALESCE($syncedHash, LastSyncedHash),
                SyncStatus = $status,
                SchemaData = $schema;
        ";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", entityType);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$hash", fileHash);
        command.Parameters.AddWithValue("$syncedHash", (object?)lastSyncedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", syncStatus);
        command.Parameters.AddWithValue("$schema", schemaData);

        command.ExecuteNonQuery();
    }

    public EntityRecord? GetEntity(string id)
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, EntityType, RelativePath, FileHash, LastSyncedHash, SyncStatus, SchemaData FROM Entities WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new EntityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)
            );
        }
        return null;
    }

    public List<EntityRecord> GetAllEntities()
    {
        var records = new List<EntityRecord>();
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, EntityType, RelativePath, FileHash, LastSyncedHash, SyncStatus, SchemaData FROM Entities";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new EntityRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)
            ));
        }
        return records;
    }

    public void DeleteEntity(string id)
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Entities WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateSyncStatus(string id, string syncStatus)
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Entities SET SyncStatus = $status WHERE Id = $id";
        command.Parameters.AddWithValue("$status", syncStatus);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void UpdateLastSyncedHash(string id, string? lastSyncedHash, string syncStatus)
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Entities SET LastSyncedHash = $syncedHash, SyncStatus = $status WHERE Id = $id";
        command.Parameters.AddWithValue("$syncedHash", (object?)lastSyncedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", syncStatus);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var connection = GetConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Entities; DELETE FROM Relationships;";
        command.ExecuteNonQuery();
    }
}

public record EntityRecord(
    string Id,
    string EntityType,
    string RelativePath,
    string FileHash,
    string? LastSyncedHash,
    string SyncStatus,
    string SchemaData
);
