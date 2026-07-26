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

        var creatures = await session.Query<CustomCreature, CustomCreature_Search>()
            .Where(cc => cc.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

        var plotThreads = await session.Query<PlotThread, PlotThread_Search>()
            .Where(pt => pt.CampaignName == campaignName)
            .Customize(x => x.WaitForNonStaleResults())
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

            ICampaignScopedEntity? entity = request.Type switch
            {
                "character" => JsonSerializer.Deserialize<Character>(request.Content, JsonOptions),
                "location" => JsonSerializer.Deserialize<Location>(request.Content, JsonOptions),
                "quest" => JsonSerializer.Deserialize<Quest>(request.Content, JsonOptions),
                "faction" => JsonSerializer.Deserialize<Faction>(request.Content, JsonOptions),
                "lore" => JsonSerializer.Deserialize<Lore>(request.Content, JsonOptions),
                "rumor" => JsonSerializer.Deserialize<Rumor>(request.Content, JsonOptions),
                "event" => JsonSerializer.Deserialize<Event>(request.Content, JsonOptions),
                "item" => JsonSerializer.Deserialize<Item>(request.Content, JsonOptions),
                "customcreature" => JsonSerializer.Deserialize<CustomCreature>(request.Content, JsonOptions),
                "plotthread" => JsonSerializer.Deserialize<PlotThread>(request.Content, JsonOptions),
                _ => throw new InvalidOperationException($"Unknown entity type '{request.Type}'.")
            };

            if (entity == null)
                return new PushResponse { Success = false, Message = $"Could not deserialize content for type '{request.Type}'." };

            // Ownership check: if an entity with this ID already exists and is scoped to a specific
            // (non-canon) campaign, it must already belong to the campaign being pushed to —
            // otherwise a push could silently re-own another campaign's entity. Canon entities
            // (null/empty CampaignName, per CampaignEntityVisibility.IsVisibleInCampaign) are
            // visible/shared across every campaign by design, so they aren't "owned" by anyone and
            // must remain pushable/updatable from any campaign.
            var existing = await session.LoadAsync<ICampaignScopedEntity>(entity.Id);
            if (existing != null && !string.IsNullOrEmpty(existing.CampaignName) && existing.CampaignName != campaignName)
            {
                return new PushResponse { Success = false, Message = $"Entity '{entity.Id}' already belongs to a different campaign and cannot be overwritten by campaign '{campaignName}'." };
            }

            if (existing != null)
            {
                // Detach the loaded instance before storing the freshly-deserialized `entity` under
                // the same ID — RavenDB's session refuses to track two different object instances
                // against one document ID.
                session.Advanced.Evict(existing);
            }

            entity.CampaignName = campaignName;
            await session.StoreAsync(entity, entity.Id);
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

            if (!string.IsNullOrWhiteSpace(request.DisplayName))
            {
                campaign.DisplayName = request.DisplayName;
            }
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

