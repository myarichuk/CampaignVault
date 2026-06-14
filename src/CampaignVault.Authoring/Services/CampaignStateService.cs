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
        await RefreshLocalStateOnlyAsync();

        // 2. Try get remote entities
        if (_clientFactory != null && !string.IsNullOrEmpty(campaignName))
        {
            try
            {
                var client = _clientFactory();
                var response = await client.GetCampaignEntitiesAsync(new GetCampaignEntitiesRequest { CampaignName = campaignName });

                // Update entities with remote info (keeping local if exists)
                foreach (var remote in response.Entities)
                {
                    var existing = Entities.FirstOrDefault(e => e.Id == remote.Id);
                    if (existing != null)
                    {
                        existing.RemoteHash = "dummy_remote_hash";
                    }
                    else
                    {
                        Entities.Add(new UnifiedEntity
                        {
                            Id = remote.Id,
                            Name = remote.Id, // Fallback name
                            EntityType = remote.Type,
                            RemoteHash = "dummy_remote_hash"
                        });
                    }
                }
            }
            catch { /* Ignore network errors for now */ }
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
}
