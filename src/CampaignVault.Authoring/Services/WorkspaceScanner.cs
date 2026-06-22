using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CampaignVault.Authoring.Services;

public class WorkspaceScanner
{
    private readonly WorkspaceDbService _dbService;
    private readonly WorkspaceParser _parser;

    public WorkspaceScanner(WorkspaceDbService dbService, WorkspaceParser parser)
    {
        _dbService = dbService;
        _parser = parser;
    }

    public async Task ScanWorkspaceAsync(string workspacePath)
    {
        var entityFolders = new[]
        {
            ("characters", "character"),
            ("locations", "location"),
            ("quests", "quest"),
            ("factions", "faction"),
            ("lore", "lore"),
            ("rumors", "rumor"),
            ("events", "event"),
            ("items", "item")
        };

        foreach (var (folder, type) in entityFolders)
        {
            var dirPath = Path.Combine(workspacePath, folder);
            if (!Directory.Exists(dirPath))
            {
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var files = Directory.GetFiles(dirPath, "*.md", SearchOption.AllDirectories);
            foreach (var filePath in files)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(workspacePath, filePath).Replace('\\', '/');
                    var content = await File.ReadAllTextAsync(filePath);
                    var fileHash = ComputeSha256Hash(content);

                    // Try to parse entity metadata
                    var (id, schemaData) = ParseEntityMetadata(content, type, filePath);

                    // Keep existing LastSyncedHash if we are updating, otherwise null
                    var existing = _dbService.GetEntity(id);
                    var lastSyncedHash = existing?.LastSyncedHash;
                    var syncStatus = existing?.SyncStatus ?? "AddedLocally";

                    if (existing != null && existing.FileHash != fileHash)
                    {
                        // File modified locally
                        syncStatus = existing.LastSyncedHash == fileHash ? "Synced" : "ModifiedLocally";
                    }

                    _dbService.UpsertEntity(
                        id,
                        type,
                        relativePath,
                        fileHash,
                        lastSyncedHash,
                        syncStatus,
                        schemaData
                    );
                }
                catch (Exception ex)
                {
                    // Fail silently for malformed files or log it
                    Console.WriteLine($"Error scanning file {filePath}: {ex.Message}");
                }
            }
        }
    }

    private (string id, string schemaData) ParseEntityMetadata(string content, string type, string filePath)
    {
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        string id = $"{type}s/{fileNameWithoutExt}".ToLower();
        string schemaData = "{}";

        try
        {
            switch (type)
            {
                case "character":
                    var character = _parser.ParseCharacter(content);
                    id = character.Id ?? id;
                    schemaData = JsonSerializer.Serialize(character);
                    break;
                case "location":
                    var location = _parser.ParseLocation(content);
                    id = location.Id ?? id;
                    schemaData = JsonSerializer.Serialize(location);
                    break;
                case "quest":
                    var quest = _parser.ParseQuest(content);
                    id = quest.Id ?? id;
                    schemaData = JsonSerializer.Serialize(quest);
                    break;
                default:
                    // For other types, parse minimal JSON frontmatter if exists, or return empty json
                    break;
            }
        }
        catch
        {
            // Fallback to name/id based on file name if parsing fails
        }

        return (id, schemaData);
    }

    private string ComputeSha256Hash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hashBytes = SHA256.HashData(bytes);
        var sb = new StringBuilder();
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
}
