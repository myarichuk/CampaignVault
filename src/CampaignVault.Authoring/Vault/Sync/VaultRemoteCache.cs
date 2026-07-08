using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Authoring.Vault.Canonical;
using CampaignVault.Grpc;

namespace CampaignVault.Authoring.Vault.Sync;

public sealed class VaultRemoteCache
{
    public const string ManifestFileName = "manifest.json";
    public const string EntitiesDirectoryName = "entities";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly EntityCanonicalizer _canonicalizer = new();

    public string CacheRootPath { get; private set; } = string.Empty;

    public void Initialize(string vaultPath)
    {
        CacheRootPath = Path.Combine(vaultPath, VaultPaths.AppConfigDirectoryName, "remote-cache");
        Directory.CreateDirectory(CacheRootPath);
        Directory.CreateDirectory(Path.Combine(CacheRootPath, EntitiesDirectoryName));
    }

    public async Task WriteFetchResultAsync(string campaignName, IEnumerable<EntityItem> entities)
    {
        if (string.IsNullOrWhiteSpace(CacheRootPath))
            throw new InvalidOperationException("Remote cache is not initialized.");

        var entityEntries = new List<RemoteCacheEntityEntry>();
        var writtenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var remote in entities)
        {
            if (string.IsNullOrWhiteSpace(remote.Id) || string.IsNullOrWhiteSpace(remote.Type))
                continue;

            var markdown = _canonicalizer.JsonToMarkdown(remote.Type, remote.Content);
            var canonicalHash = _canonicalizer.ComputeCanonicalHashFromJson(remote.Type, remote.Content);
            var entityPath = GetEntityCachePath(remote.Id);

            var directory = Path.GetDirectoryName(entityPath)!;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(entityPath, markdown);

            entityEntries.Add(new RemoteCacheEntityEntry
            {
                Id = remote.Id,
                Type = remote.Type,
                CanonicalHash = canonicalHash
            });
            writtenIds.Add(remote.Id);
        }

        foreach (var stale in Directory.EnumerateFiles(
                     Path.Combine(CacheRootPath, EntitiesDirectoryName),
                     "*.md",
                     SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(CacheRootPath, EntitiesDirectoryName), stale)
                .Replace('\\', '/');
            var entityId = relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? relative[..^3]
                : relative;
            if (!writtenIds.Contains(entityId))
                File.Delete(stale);
        }

        var manifest = new RemoteCacheManifest
        {
            FetchedAt = DateTimeOffset.UtcNow,
            CampaignName = campaignName,
            Entities = entityEntries
                .OrderBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var manifestPath = Path.Combine(CacheRootPath, ManifestFileName);
        var tmpManifestPath = manifestPath + ".tmp";
        await File.WriteAllTextAsync(tmpManifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + "\n");
        File.Move(tmpManifestPath, manifestPath, overwrite: true);
    }

    public RemoteCacheManifestReadResult ReadManifest()
    {
        if (string.IsNullOrWhiteSpace(CacheRootPath))
            return new RemoteCacheManifestReadResult(null, false, null);

        var manifestPath = Path.Combine(CacheRootPath, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new RemoteCacheManifestReadResult(null, false, null);

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<RemoteCacheManifest>(json, JsonOptions);
            if (manifest == null)
            {
                return new RemoteCacheManifestReadResult(
                    null,
                    true,
                    $"Could not parse {ManifestFileName}.");
            }

            return new RemoteCacheManifestReadResult(manifest, false, null);
        }
        catch (Exception ex)
        {
            return new RemoteCacheManifestReadResult(
                null,
                true,
                "The remote cache manifest is corrupt. Fetch again from the Vault Sync pane.");
        }
    }

    public bool TryReadEntityMarkdown(string entityId, out string markdown, out string canonicalHash)
    {
        markdown = string.Empty;
        canonicalHash = string.Empty;

        if (string.IsNullOrWhiteSpace(CacheRootPath))
            return false;

        var path = GetEntityCachePath(entityId);
        if (!File.Exists(path))
            return false;

        markdown = File.ReadAllText(path);
        var entityType = InferEntityTypeFromId(entityId);
        canonicalHash = entityType != null
            ? _canonicalizer.ComputeCanonicalHash(entityType, markdown)
            : VaultContentHash.Compute(markdown);
        return true;
    }

    public IReadOnlyDictionary<string, RemoteCacheEntityEntry> ReadEntityIndex(out RemoteCacheManifestReadResult manifestResult)
    {
        manifestResult = ReadManifest();
        if (manifestResult.IsCorrupt || manifestResult.Manifest?.Entities == null)
            return new Dictionary<string, RemoteCacheEntityEntry>(StringComparer.OrdinalIgnoreCase);

        return manifestResult.Manifest.Entities.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
    }

    public string GetEntityCachePath(string entityId)
    {
        var relative = entityId.Replace('\\', '/').Trim('/');
        var path = Path.Combine(CacheRootPath, EntitiesDirectoryName, relative + ".md");
        return path;
    }

    private static string? InferEntityTypeFromId(string entityId)
    {
        var slash = entityId.IndexOf('/');
        if (slash <= 0)
            return null;

        var folder = entityId[..slash];
        foreach (var (folderName, entityType) in VaultPaths.EntityFolders)
        {
            if (string.Equals(folder, folderName, StringComparison.OrdinalIgnoreCase))
                return entityType;
        }

        return null;
    }
}

public sealed class RemoteCacheManifest
{
    public DateTimeOffset FetchedAt { get; set; }
    public string CampaignName { get; set; } = string.Empty;
    public List<RemoteCacheEntityEntry> Entities { get; set; } = [];
}

public sealed class RemoteCacheEntityEntry
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string CanonicalHash { get; set; } = string.Empty;
}

public sealed record RemoteCacheManifestReadResult(
    RemoteCacheManifest? Manifest,
    bool IsCorrupt,
    string? ErrorMessage);