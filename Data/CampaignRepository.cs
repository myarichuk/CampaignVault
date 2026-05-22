using Raven.Client.Documents;
using CampaignVault.Models;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Indexes;

namespace CampaignVault.Data;

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

        // 1. Try direct ID load (fastest path)
        var character = await session.LoadAsync<Character>(identifier);
        if (character != null) return character;

        // 2. Exact name match
        character = await session.Query<Character>()
            .FirstOrDefaultAsync(x => x.Name == identifier);
        if (character != null) return character;

        // 3. Fuzzy search on Name + Notes (using the new index)
        character = await session.Advanced.AsyncDocumentQuery<Character, Character_Search>()
            .WhereEquals(x => x.Name, identifier).Fuzzy(0.4m)
            .FirstOrDefaultAsync();

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
        if (sessionCharacter == null) return false;

        // Robust field mapping (easy to extend)
        foreach (var (key, value) in updates)
        {
            switch (key.ToLowerInvariant())
            {
                case "currenthp":
                case "current_hp":
                    sessionCharacter.CurrentHp = Convert.ToInt32(value);
                    break;
                case "maxhp":
                case "max_hp":
                    sessionCharacter.MaxHp = Convert.ToInt32(value);
                    break;
                case "notes":
                    sessionCharacter.Notes = value?.ToString();
                    break;
                case "needs":
                    if (value is Dictionary<string, object> needsDict)
                    {
                        foreach (var (needKey, needValue) in needsDict)
                        {
                            sessionCharacter.Needs[needKey] = Convert.ToInt32(needValue);
                        }
                    }
                    break;
                // Add more fields here as your Character model grows
                default:
                    // Optional: log unknown key or ignore
                    break;
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
            q = q.Where(x => x.Summary.Contains(query)); // consider Search index later
        }
        if (!string.IsNullOrEmpty(type))
        {
            q = q.Where(x => x.Type == type);
        }

        return await q.OrderByDescending(x => x.Timestamp).Take(limit).ToListAsync();
    }

    public void Dispose()
    {
        // _store?.Dispose(); // only if this repo owns the store
    }
}
