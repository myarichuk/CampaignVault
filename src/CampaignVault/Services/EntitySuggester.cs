using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents.Session;

namespace CampaignVault.Services;

public interface IEntitySuggester
{
    Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign);
    Task<List<Character>> SuggestCharactersAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign);
    Task<List<Item>> SuggestItemsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign);
    Task<List<Faction>> SuggestFactionsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign);
    Task<List<Quest>> SuggestQuestsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign);
}

internal class EntitySuggester : IEntitySuggester
{
    private readonly ILocalEmbeddingService _embeddingService;
    private readonly ILogger<EntitySuggester> _logger;

    public EntitySuggester(ILocalEmbeddingService embeddingService, ILogger<EntitySuggester> logger)
    {
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<List<Location>> SuggestLocationsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign)
    {
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("locations/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["locations/".Length..];
        }
        else if (cleanQuery.StartsWith("locs/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["locs/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "locations/");

        try
        {
            var suggestions = await session.Query<Location, Location_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Location, Location_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var item in byName)
                {
                    if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(item);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestLocationsAsync timed out waiting for index; returning empty results.");
            return [];
        }
    }

    public async Task<List<Character>> SuggestCharactersAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign)
    {
        var rawQuery = CanonicalId.NormalizeAlias(nameQuery.Trim());
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("chars/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["chars/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "chars/");

        try
        {
            var suggestions = await session.Query<Character, Character_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Character, Character_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var item in byName)
                {
                    if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(item);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestCharactersAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    public async Task<List<Item>> SuggestItemsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign)
    {
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("items/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["items/".Length..];
        }
        else if (cleanQuery.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["item/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "items/");

        try
        {
            var suggestions = await session.Query<Item, Item_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Item, Item_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var item in byName)
                {
                    if (suggestions.All(s => s.Id != item.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(item);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestItemsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    public async Task<List<Faction>> SuggestFactionsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign)
    {
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("factions/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["factions/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "factions/");

        try
        {
            var suggestions = await session.Query<Faction, Faction_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Faction, Faction_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                    .Search(x => x.Name, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var f in byName)
                {
                    if (suggestions.All(s => s.Id != f.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(f);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestFactionsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    public async Task<List<Quest>> SuggestQuestsAsync(IAsyncDocumentSession session, string nameQuery, string effectiveCampaign)
    {
        var rawQuery = nameQuery.Trim();
        var cleanQuery = rawQuery;
        if (cleanQuery.StartsWith("quests/", StringComparison.OrdinalIgnoreCase))
        {
            cleanQuery = cleanQuery["quests/".Length..];
        }

        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            return [];
        }

        var canonicalIdPrefix = BuildCanonicalIdPrefix(cleanQuery, "quests/");

        try
        {
            var suggestions = await session.Query<Quest, Quest_Search>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                .Where(x => x.Id.StartsWith(rawQuery) || x.Id.StartsWith(canonicalIdPrefix))
                .Take(3).ToListAsync();

            if (suggestions.Count < 3)
            {
                var queryVector = await _embeddingService.GenerateEmbeddingAsync(cleanQuery);
                var byNameQuery = session.Query<Quest, Quest_Search>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                    .Where(x => x.CampaignName == effectiveCampaign || x.CampaignName == null)
                    .Search(x => x.Title, cleanQuery + "*");

                if (queryVector is { Length: EmbeddingModelPaths.VectorDimensions })
                {
                    byNameQuery = byNameQuery.VectorSearch(f => f.WithField(x => x.SemanticVector), v => v.ByEmbedding(queryVector));
                }

                var byName = await byNameQuery.Take(3).ToListAsync();

                foreach (var q in byName)
                {
                    if (suggestions.All(s => s.Id != q.Id) && suggestions.Count < 3)
                    {
                        suggestions.Add(q);
                    }
                }
            }

            return suggestions;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "SuggestQuestsAsync timed out waiting for index; returning empty.");
            return [];
        }
    }

    private static string BuildCanonicalIdPrefix(string cleanQuery, string prefix) =>
        cleanQuery.Contains('/', StringComparison.Ordinal) ? cleanQuery : prefix + cleanQuery;
}
