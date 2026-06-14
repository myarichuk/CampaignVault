using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Grpc;

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
        Entities.Clear();

        // 1. Get local entities
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

        // 2. Try get remote entities
        if (_clientFactory != null && !string.IsNullOrEmpty(campaignName))
        {
            try
            {
                var client = _clientFactory();
                var response = await client.GetCampaignEntitiesAsync(new GetCampaignEntitiesRequest { CampaignName = campaignName });

                // For this implementation, we will use a dummy hash for remote until full logic is ported
                foreach (var remote in response.Entities)
                {
                    if (idMap.TryGetValue(remote.Id, out var existing))
                    {
                        // In a real scenario, deserialize and hash. Using "dummy" to indicate existence.
                        existing.RemoteHash = "dummy_remote_hash";
                    }
                    else
                    {
                        idMap[remote.Id] = new UnifiedEntity
                        {
                            Id = remote.Id,
                            Name = remote.Id, // Fallback name
                            EntityType = remote.Type,
                            RemoteHash = "dummy_remote_hash"
                        };
                    }
                }
            }
            catch { /* Ignore network errors for now */ }
        }

        foreach (var entity in idMap.Values.OrderBy(e => e.EntityType).ThenBy(e => e.Name))
        {
            Entities.Add(entity);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
