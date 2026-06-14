using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        // Sync local collection with idMap (avoiding full Clear if possible, but for simplicity now...)
        Entities.Clear();
        foreach (var entity in idMap.Values.OrderBy(e => e.EntityType).ThenBy(e => e.Name))
        {
            Entities.Add(entity);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshLocalStateOnlyAsync()
    {
        Entities.Clear();

        // 1. Get local entities
        var localEntities = _dbService.GetAllEntities();

        foreach (var local in localEntities)
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

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private string DeserializeRemoteToMarkdown(EntityItem remote)
    {
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .Build();

        if (remote.Type == "character")
        {
            var c = JsonSerializer.Deserialize<Character>(remote.Content);
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
            var l = JsonSerializer.Deserialize<Location>(remote.Content);
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
            var q = JsonSerializer.Deserialize<Quest>(remote.Content);
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
