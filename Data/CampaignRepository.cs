using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

public class Lore_Search : AbstractIndexCreationTask<Lore>
{
    public Lore_Search()
    {
        Map = lores => from lore in lores
                       select new
                       {
                           lore.Title,
                           lore.Content,
                           lore.Tags,
                           lore.Category
                       };
        
        SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Lucene;

        Index(x => x.Title, FieldIndexing.Search);
        Index(x => x.Content, FieldIndexing.Search);
    }
}

public class CampaignRepository : IDisposable
{
    private readonly IDocumentStore _store;

    public CampaignRepository(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<Character?> GetCharacterAsync(string identifier)
    {
        using var session = _store.OpenAsyncSession();
        var character = await session.LoadAsync<Character>(identifier);
        if (character == null)
        {
            character = await session.Query<Character>()
                .FirstOrDefaultAsync(x => x.Name == identifier);
        }
        return character;
    }

    public async Task UpsertCharacterAsync(Character character)
    {
        using var session = _store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions
        {
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes
        });
        character.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(character);
        await session.SaveChangesAsync();
    }

    public async Task<bool> UpdateCharacterAsync(string identifier, Dictionary<string, object> updates)
    {
        using var session = _store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions
        {
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes
        });
        
        var character = await GetCharacterAsync(identifier);
        if (character == null) return false;

        var sessionCharacter = await session.LoadAsync<Character>(character.Id);

        if (updates.TryGetValue("currentHp", out var hp)) sessionCharacter.CurrentHp = Convert.ToInt32(hp);
        if (updates.TryGetValue("maxHp", out var mhp)) sessionCharacter.MaxHp = Convert.ToInt32(mhp);
        if (updates.TryGetValue("notes", out var notes)) sessionCharacter.Notes = notes?.ToString();
        
        if (updates.TryGetValue("needs", out var needsObj) && needsObj is Dictionary<string, object> needsDict)
        {
            foreach (var (key, value) in needsDict)
            {
                sessionCharacter.Needs[key] = Convert.ToInt32(value);
            }
        }

        sessionCharacter.LastUpdated = DateTime.UtcNow;
        await session.SaveChangesAsync();
        return true;
    }

    public async Task UpsertLoreAsync(Lore lore)
    {
        using var session = _store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions
        {
            OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes
        });
        lore.LastUpdated = DateTime.UtcNow;
        await session.StoreAsync(lore);
        await session.SaveChangesAsync();
    }

    public async Task<IEnumerable<Lore>> QueryLoreAsync(string? query, string[]? tags, string? category, int limit = 5)
    {
        using var session = _store.OpenAsyncSession();
        // Query the static Lucene index
        var q = session.Advanced.AsyncDocumentQuery<Lore, Lore_Search>();
        
        if (!string.IsNullOrEmpty(query))
        {
            q = q.OpenSubclause()
                 .WhereEquals(x => x.Title, query).Fuzzy(0.4m)
                 .OrElse()
                 .WhereEquals(x => x.Content, query).Fuzzy(0.4m)
                 .CloseSubclause();
        }
        
        if (tags != null && tags.Length > 0)
        {
            foreach (var tag in tags)
            {
                q = q.AndAlso().ContainsAny(x => x.Tags, new[] { tag });
            }
        }
        
        if (!string.IsNullOrEmpty(category))
        {
            q = q.AndAlso().WhereEquals(x => x.Category, category);
        }
        
        return await q.Take(limit).ToListAsync();
    }

    public async Task LogEventAsync(Event @event)
    {
        using var session = _store.OpenAsyncSession();
        await session.StoreAsync(@event);
        await session.SaveChangesAsync();
    }

    public async Task<IEnumerable<Event>> QueryEventsAsync(string? query, string? type, int limit = 10)
    {
        using var session = _store.OpenAsyncSession();
        var q = session.Query<Event>();

        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(x => x.Summary.Contains(query));
        }

        if (!string.IsNullOrEmpty(type))
        {
            q = q.Where(x => x.Type == type);
        }

        return await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
    }

    public void Dispose()
    {
    }
}
