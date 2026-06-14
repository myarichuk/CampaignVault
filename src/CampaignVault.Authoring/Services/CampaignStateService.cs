using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Grpc;
using CampaignVault.Models;

namespace CampaignVault.Authoring.Services;

public class CampaignStateService
{
    private readonly WorkspaceDbService _dbService;
    private Func<CampaignSync.CampaignSyncClient>? _clientFactory;

    public ObservableCollection<UnifiedEntity> Entities { get; } = new();
    public event EventHandler? StateChanged;

    private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

    public CampaignStateService(WorkspaceDbService dbService)
    {
        _dbService = dbService;
    }

    public void SetClientFactory(Func<CampaignSync.CampaignSyncClient> factory)
    {
        _clientFactory = factory;
    }

    public async Task RefreshStateAsync(string campaignName)
    {
        await _refreshLock.WaitAsync();
        try
        {
            // 1. Get local entities from SQLite
            var localEntities = _dbService.GetAllEntities();
            var idMap = new Dictionary<string, UnifiedEntity>();

            foreach (var local in localEntities)
            {
                idMap[local.Id] = new UnifiedEntity
                {
                    Id = local.Id,
                    Name = System.IO.Path.GetFileNameWithoutExtension(local.RelativePath),
                    EntityType = local.EntityType,
                    LocalHash = local.FileHash,
                    LastSyncedHash = local.LastSyncedHash,
                    RelativePath = local.RelativePath
                };
            }

            // 2. Try get remote entities from gRPC
            if (_clientFactory != null && !string.IsNullOrEmpty(campaignName))
            {
                try
                {
                    var client = _clientFactory();
                    var response = await client.GetCampaignEntitiesAsync(new GetCampaignEntitiesRequest { CampaignName = campaignName });

                    foreach (var remote in response.Entities)
                    {
                        var remoteMarkdown = DeserializeRemoteToMarkdown(remote);
                        var remoteHash = ComputeSha256Hash(remoteMarkdown);

                        if (idMap.TryGetValue(remote.Id, out var existing))
                        {
                            existing.RemoteHash = remoteHash;
                            existing.RemoteMarkdown = remoteMarkdown;
                        }
                        else
                        {
                            idMap[remote.Id] = new UnifiedEntity
                            {
                                Id = remote.Id,
                                Name = remote.Id, // Fallback name
                                EntityType = remote.Type,
                                RemoteHash = remoteHash,
                                RemoteMarkdown = remoteMarkdown
                            };
                        }
                    }
                }
                catch { /* Ignore network errors for now */ }
            }

            SyncEntitiesCollection(idMap.Values);

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task RefreshLocalStateOnlyAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            // 1. Get local entities
            var localEntities = _dbService.GetAllEntities();

            // Create a set of local IDs
            var localIdSet = new HashSet<string>();

            foreach (var local in localEntities)
            {
                localIdSet.Add(local.Id);
                var existing = Entities.FirstOrDefault(e => e.Id == local.Id);
                if (existing != null)
                {
                    existing.LocalHash = local.FileHash;
                    existing.LastSyncedHash = local.LastSyncedHash;
                    existing.RelativePath = local.RelativePath;
                    // Update name in case it changed locally
                    existing.Name = System.IO.Path.GetFileNameWithoutExtension(local.RelativePath);
                }
                else
                {
                    Entities.Add(new UnifiedEntity
                    {
                        Id = local.Id,
                        Name = System.IO.Path.GetFileNameWithoutExtension(local.RelativePath),
                        EntityType = local.EntityType,
                        LocalHash = local.FileHash,
                        LastSyncedHash = local.LastSyncedHash,
                        RelativePath = local.RelativePath
                    });
                }
            }

            // Remove entities that are no longer local AND have no remote data
            var toRemove = Entities.Where(e => !localIdSet.Contains(e.Id) && e.RemoteHash == null).ToList();
            foreach (var r in toRemove)
            {
                Entities.Remove(r);
            }

            // For entities that exist remotely but were deleted locally, clear local fields
            var deletedLocally = Entities.Where(e => !localIdSet.Contains(e.Id) && e.RemoteHash != null).ToList();
            foreach (var d in deletedLocally)
            {
                d.LocalHash = null;
                d.LastSyncedHash = null;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void SyncEntitiesCollection(IEnumerable<UnifiedEntity> updatedEntities)
    {
        var updatedById = updatedEntities.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in Entities.ToList())
        {
            if (!updatedById.ContainsKey(existing.Id))
                Entities.Remove(existing);
        }

        foreach (var updated in updatedById.Values.OrderBy(e => e.EntityType).ThenBy(e => e.Name))
        {
            var existing = Entities.FirstOrDefault(e => string.Equals(e.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Entities.Add(updated);
                continue;
            }

            existing.Name = updated.Name;
            existing.EntityType = updated.EntityType;
            existing.LocalHash = updated.LocalHash;
            existing.RemoteHash = updated.RemoteHash;
            existing.LastSyncedHash = updated.LastSyncedHash;
            existing.RelativePath = updated.RelativePath;
            existing.RemoteMarkdown = updated.RemoteMarkdown;
        }
    }

    private string DeserializeRemoteToMarkdown(EntityItem remote)
    {
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (remote.Type == "character")
        {
            var c = JsonSerializer.Deserialize<Character>(remote.Content, opts);
            if (c != null)
            {
                var copy = new Character
                {
                    Id = c.Id,
                    Name = c.Name,
                    ClassLevel = c.ClassLevel,
                    CurrentHp = c.CurrentHp,
                    MaxHp = c.MaxHp,
                    DistinctiveFeatures = c.DistinctiveFeatures,
                    CurrentAppearance = c.CurrentAppearance,
                    VisualTags = c.VisualTags,
                    KeepAlive = c.KeepAlive,
                    Schedule = c.Schedule,
                    CurrentLocationId = c.CurrentLocationId,
                    CurrentActivity = c.CurrentActivity,
                    Psychology = c.Psychology,
                    Social = c.Social,
                    Needs = c.Needs,
                    SystemStats = c.SystemStats,
                    LastUpdated = c.LastUpdated,
                    CampaignName = c.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{c.Notes ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }
        else if (remote.Type == "location")
        {
            var l = JsonSerializer.Deserialize<Location>(remote.Content, opts);
            if (l != null)
            {
                var copy = new Location
                {
                    Id = l.Id,
                    Name = l.Name,
                    Type = l.Type,
                    ParentLocationId = l.ParentLocationId,
                    Exits = l.Exits,
                    PointsOfInterest = l.PointsOfInterest,
                    AmbientCrowd = l.AmbientCrowd,
                    LastVisitedDay = l.LastVisitedDay,
                    Metadata = l.Metadata,
                    CurrentState = l.CurrentState,
                    VisualTags = l.VisualTags,
                    DistinctiveFeatures = l.DistinctiveFeatures,
                    LastUpdated = l.LastUpdated,
                    ControllingFactionId = l.ControllingFactionId,
                    DangerModifier = l.DangerModifier,
                    CampaignName = l.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{l.Description ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }
        else if (remote.Type == "quest")
        {
            var q = JsonSerializer.Deserialize<Quest>(remote.Content, opts);
            if (q != null)
            {
                var copy = new Quest
                {
                    Id = q.Id,
                    Title = q.Title,
                    GiverId = q.GiverId,
                    Objectives = q.Objectives,
                    OverallState = q.OverallState,
                    Category = q.Category,
                    Urgency = q.Urgency,
                    RelatedLocationIds = q.RelatedLocationIds,
                    RelatedFactionIds = q.RelatedFactionIds,
                    VisibleToCharacterIds = q.VisibleToCharacterIds,
                    DeadlineDay = q.DeadlineDay,
                    LastUpdatedDay = q.LastUpdatedDay,
                    LastUpdated = q.LastUpdated,
                    CampaignName = q.CampaignName
                };
                var yaml = serializer.Serialize(copy);
                return $"---\n{yaml}---\n\n{q.DmNotes ?? string.Empty}".ReplaceLineEndings("\n");
            }
        }

        return string.Empty;
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
