using System.Text.Json;
using CampaignVault.Data;
using CampaignVault.Grpc;
using CampaignVault.Models;
using Grpc.Core;

namespace CampaignVault.Services;

public class CampaignSyncService(IDocumentStore documentStore, CampaignDocumentKeys keys)
    : CampaignSync.CampaignSyncBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        
        var characters = await session.Query<Character, Character_Search>()
            .Where(c => c.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var locations = await session.Query<Location, Location_Search>()
            .Where(l => l.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var quests = await session.Query<Quest, Quest_Search>()
            .Where(q => q.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var factions = await session.Query<Faction, Faction_Search>()
            .Where(f => f.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var lore = await session.Query<Lore, Lore_Search>()
            .Where(l => l.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var rumors = await session.Query<Rumor, Rumor_Search>()
            .Where(r => r.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var items = await session.Query<Item>()
            .Where(i => i.CampaignName == campaignName)
            .ToListAsync();
        foreach (var i in items)
        {
            response.Entities.Add(new EntityItem
            {
                Id = i.Id,
                Type = "item",
                Content = JsonSerializer.Serialize(i)
            });
        }

        var creatures = await session.Query<CustomCreature>()
            .Where(cc => cc.CampaignName == campaignName)
            .ToListAsync();
        foreach (var cc in creatures)
        {
            response.Entities.Add(new EntityItem
            {
                Id = cc.Id,
                Type = "customcreature",
                Content = JsonSerializer.Serialize(cc)
            });
        }

        var plotThreads = await session.Query<PlotThread>()
            .Where(pt => pt.CampaignName == campaignName)
            .ToListAsync();
        foreach (var pt in plotThreads)
        {
            response.Entities.Add(new EntityItem
            {
                Id = pt.Id,
                Type = "plotthread",
                Content = JsonSerializer.Serialize(pt)
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
            bool stored;

            if (request.Type == "character")
            {
                var charData = JsonSerializer.Deserialize<Character>(request.Content, JsonOptions);
                stored = charData != null;
                if (charData != null)
                {
                    charData.CampaignName = campaignName;
                    await session.StoreAsync(charData, charData.Id);
                }
            }
            else if (request.Type == "location")
            {
                var locData = JsonSerializer.Deserialize<Location>(request.Content, JsonOptions);
                stored = locData != null;
                if (locData != null)
                {
                    locData.CampaignName = campaignName;
                    await session.StoreAsync(locData, locData.Id);
                }
            }
            else if (request.Type == "quest")
            {
                var questData = JsonSerializer.Deserialize<Quest>(request.Content, JsonOptions);
                stored = questData != null;
                if (questData != null)
                {
                    questData.CampaignName = campaignName;
                    await session.StoreAsync(questData, questData.Id);
                }
            }
            else if (request.Type == "faction")
            {
                var factionData = JsonSerializer.Deserialize<Faction>(request.Content, JsonOptions);
                stored = factionData != null;
                if (factionData != null)
                {
                    factionData.CampaignName = campaignName;
                    await session.StoreAsync(factionData, factionData.Id);
                }
            }
            else if (request.Type == "lore")
            {
                var loreData = JsonSerializer.Deserialize<Lore>(request.Content, JsonOptions);
                stored = loreData != null;
                if (loreData != null)
                {
                    loreData.CampaignName = campaignName;
                    await session.StoreAsync(loreData, loreData.Id);
                }
            }
            else if (request.Type == "rumor")
            {
                var rumorData = JsonSerializer.Deserialize<Rumor>(request.Content, JsonOptions);
                stored = rumorData != null;
                if (rumorData != null)
                {
                    rumorData.CampaignName = campaignName;
                    await session.StoreAsync(rumorData, rumorData.Id);
                }
            }
            else if (request.Type == "event")
            {
                var eventData = JsonSerializer.Deserialize<Event>(request.Content, JsonOptions);
                stored = eventData != null;
                if (eventData != null)
                {
                    eventData.CampaignName = campaignName;
                    await session.StoreAsync(eventData, eventData.Id);
                }
            }
            else if (request.Type == "item")
            {
                var itemData = JsonSerializer.Deserialize<Item>(request.Content, JsonOptions);
                stored = itemData != null;
                if (itemData != null)
                {
                    itemData.CampaignName = campaignName;
                    await session.StoreAsync(itemData, itemData.Id);
                }
            }
            else if (request.Type == "customcreature")
            {
                var creatureData = JsonSerializer.Deserialize<CustomCreature>(request.Content, JsonOptions);
                stored = creatureData != null;
                if (creatureData != null)
                {
                    creatureData.CampaignName = campaignName;
                    await session.StoreAsync(creatureData, creatureData.Id);
                }
            }
            else if (request.Type == "plotthread")
            {
                var plotThreadData = JsonSerializer.Deserialize<PlotThread>(request.Content, JsonOptions);
                stored = plotThreadData != null;
                if (plotThreadData != null)
                {
                    plotThreadData.CampaignName = campaignName;
                    await session.StoreAsync(plotThreadData, plotThreadData.Id);
                }
            }
            else
            {
                return new PushResponse { Success = false, Message = $"Unknown entity type '{request.Type}'." };
            }

            if (!stored)
                return new PushResponse { Success = false, Message = $"Could not deserialize content for type '{request.Type}'." };

            await session.SaveChangesAsync();

            return new PushResponse { Success = true, Message = "Successfully pushed." };
        }
        catch (Exception ex)
        {
            return new PushResponse { Success = false, Message = ex.Message };
        }
    }

    public override async Task<PushResponse> DeleteCampaignEntity(
        DeleteCampaignEntityRequest request,
        ServerCallContext context)
    {
        try
        {
            using var session = documentStore.OpenAsyncSession();
            var entity = await session.LoadAsync<ICampaignScopedEntity>(request.Id);
            if (entity == null)
                return new PushResponse { Success = false, Message = $"Entity '{request.Id}' was not found." };

            if (entity.CampaignName != request.CampaignName)
                return new PushResponse { Success = false, Message = $"Entity '{request.Id}' does not belong to campaign '{request.CampaignName}'." };

            session.Delete(request.Id);
            await session.SaveChangesAsync();
            return new PushResponse { Success = true, Message = "Successfully deleted." };
        }
        catch (Exception ex)
        {
            return new PushResponse { Success = false, Message = ex.Message };
        }
    }

    public override async Task<PushResponse> UpdateCampaignMetadata(UpdateCampaignMetadataRequest request, ServerCallContext context)
    {
        try
        {
            using var session = documentStore.OpenAsyncSession();

            var campaign = await session.Query<Campaign>()
                .FirstOrDefaultAsync(c => c.Name == request.CampaignName);

            if (campaign == null)
                return new PushResponse { Success = false, Message = $"Campaign '{request.CampaignName}' not found." };

            campaign.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null! : request.DisplayName;
            campaign.NarrativeFocus = request.NarrativeFocus?.ToList() ?? [];

            await session.SaveChangesAsync();

            return new PushResponse
            {
                Success = true,
                Message = $"Campaign '{request.CampaignName}' metadata updated successfully."
            };
        }
        catch (Exception ex)
        {
            return new PushResponse
            {
                Success = false,
                Message = $"Failed to update campaign metadata: {ex.Message}"
            };
        }
    }
}

