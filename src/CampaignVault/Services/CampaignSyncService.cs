using System.Text.Json;
using CampaignVault.Data;
using CampaignVault.Grpc;
using CampaignVault.Models;
using Grpc.Core;
using Raven.Client.Documents;

namespace CampaignVault.Services;

public class CampaignSyncService(IDocumentStore documentStore, CampaignDocumentKeys keys)
    : CampaignSync.CampaignSyncBase
{
    private readonly CampaignDocumentKeys _keys = keys;

    public override async Task<CampaignListResponse> GetCampaigns(EmptyRequest request, ServerCallContext context)
    {
        var response = new CampaignListResponse();

        using var session = documentStore.OpenAsyncSession();
        var campaigns = await session.Query<Campaign>().ToListAsync();
        foreach (var c in campaigns)
        {
            response.Campaigns.Add(new CampaignItem
            {
                Name = c.Name,
                Ruleset = c.System.ToString()
            });
        }

        return response;
    }

    public override async Task<EntityListResponse> GetCampaignEntities(GetCampaignEntitiesRequest request, ServerCallContext context)
    {
        var response = new EntityListResponse();
        var campaignName = request.CampaignName;

        using var session = documentStore.OpenAsyncSession();
        
        var characters = await session.Query<Character>()
            .Where(c => c.CampaignName == campaignName || c.CampaignName == null || c.CampaignName == "")
            .ToListAsync();
        foreach (var c in characters)
        {
            response.Entities.Add(new EntityItem
            {
                Id = c.Id,
                Type = "character",
                Content = JsonSerializer.Serialize(c)
            });
        }

        var locations = await session.Query<Location>()
            .Where(l => l.CampaignName == campaignName || l.CampaignName == null || l.CampaignName == "")
            .ToListAsync();
        foreach (var l in locations)
        {
            response.Entities.Add(new EntityItem
            {
                Id = l.Id,
                Type = "location",
                Content = JsonSerializer.Serialize(l)
            });
        }

        var quests = await session.Query<Quest>()
            .Where(q => q.CampaignName == campaignName || q.CampaignName == null || q.CampaignName == "")
            .ToListAsync();
        foreach (var q in quests)
        {
            response.Entities.Add(new EntityItem
            {
                Id = q.Id,
                Type = "quest",
                Content = JsonSerializer.Serialize(q)
            });
        }

        var factions = await session.Query<Faction>()
            .Where(f => f.CampaignName == campaignName || f.CampaignName == null || f.CampaignName == "")
            .ToListAsync();
        foreach (var f in factions)
        {
            response.Entities.Add(new EntityItem
            {
                Id = f.Id,
                Type = "faction",
                Content = JsonSerializer.Serialize(f)
            });
        }

        var lore = await session.Query<Lore>()
            .Where(l => l.CampaignName == campaignName || l.CampaignName == null || l.CampaignName == "")
            .ToListAsync();
        foreach (var l in lore)
        {
            response.Entities.Add(new EntityItem
            {
                Id = l.Id,
                Type = "lore",
                Content = JsonSerializer.Serialize(l)
            });
        }

        var rumors = await session.Query<Rumor>()
            .Where(r => r.CampaignName == campaignName || r.CampaignName == null || r.CampaignName == "")
            .ToListAsync();
        foreach (var r in rumors)
        {
            response.Entities.Add(new EntityItem
            {
                Id = r.Id,
                Type = "rumor",
                Content = JsonSerializer.Serialize(r)
            });
        }

        var events = await session.Query<Event>()
            .Where(e => e.CampaignName == campaignName)
            .ToListAsync();
        foreach (var e in events)
        {
            response.Entities.Add(new EntityItem
            {
                Id = e.Id,
                Type = "event",
                Content = JsonSerializer.Serialize(e)
            });
        }

        return response;
    }

    public override async Task<PushResponse> PushCampaignEntity(PushCampaignEntityRequest request, ServerCallContext context)
    {
        try
        {
            using var session = documentStore.OpenAsyncSession();
            var campaignName = request.CampaignName;
            
            if (request.Type == "character")
            {
                var charData = JsonSerializer.Deserialize<Character>(request.Content);
                if (charData != null)
                {
                    charData.CampaignName = campaignName;
                    await session.StoreAsync(charData, charData.Id);
                }
            }
            else if (request.Type == "location")
            {
                var locData = JsonSerializer.Deserialize<Location>(request.Content);
                if (locData != null)
                {
                    locData.CampaignName = campaignName;
                    await session.StoreAsync(locData, locData.Id);
                }
            }
            else if (request.Type == "quest")
            {
                var questData = JsonSerializer.Deserialize<Quest>(request.Content);
                if (questData != null)
                {
                    questData.CampaignName = campaignName;
                    await session.StoreAsync(questData, questData.Id);
                }
            }
            else if (request.Type == "faction")
            {
                var factionData = JsonSerializer.Deserialize<Faction>(request.Content);
                if (factionData != null)
                {
                    factionData.CampaignName = campaignName;
                    await session.StoreAsync(factionData, factionData.Id);
                }
            }
            else if (request.Type == "lore")
            {
                var loreData = JsonSerializer.Deserialize<Lore>(request.Content);
                if (loreData != null)
                {
                    loreData.CampaignName = campaignName;
                    await session.StoreAsync(loreData, loreData.Id);
                }
            }
            else if (request.Type == "rumor")
            {
                var rumorData = JsonSerializer.Deserialize<Rumor>(request.Content);
                if (rumorData != null)
                {
                    rumorData.CampaignName = campaignName;
                    await session.StoreAsync(rumorData, rumorData.Id);
                }
            }
            else if (request.Type == "event")
            {
                var eventData = JsonSerializer.Deserialize<Event>(request.Content);
                if (eventData != null)
                {
                    eventData.CampaignName = campaignName;
                    await session.StoreAsync(eventData, eventData.Id);
                }
            }

            await session.SaveChangesAsync();

            return new PushResponse { Success = true, Message = "Successfully pushed." };
        }
        catch (Exception ex)
        {
            return new PushResponse { Success = false, Message = ex.Message };
        }
    }
}

