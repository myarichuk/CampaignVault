using LiteDB;
using CampaignVault.Models;

namespace CampaignVault.Data;

public class CampaignRepository : IDisposable
{
    private readonly LiteDatabase _db;

    public CampaignRepository(string dbPath)
    {
        _db = new LiteDatabase(dbPath);
        
        var characters = _db.GetCollection("characters");
        characters.EnsureIndex("Name");

        var lore = _db.GetCollection("lore");
        lore.EnsureIndex("Tags");
        lore.EnsureIndex("Title");
        lore.EnsureIndex("Keywords");
    }

    public BsonDocument? GetCharacter(string identifier)
    {
        var col = _db.GetCollection("characters");
        return col.FindOne(Query.Or(Query.EQ("_id", identifier), Query.Contains("Name", identifier)));
    }

    public void UpsertCharacter(Character character)
    {
        var col = _db.GetCollection("characters");
        var doc = _db.Mapper.ToDocument(character);
        doc["LastUpdated"] = DateTime.UtcNow;
        col.Upsert(doc);
    }

    public bool UpdateCharacter(string identifier, Dictionary<string, object> updates)
    {
        var col = _db.GetCollection("characters");
        var doc = col.FindOne(Query.Or(Query.EQ("_id", identifier), Query.Contains("Name", identifier)));
        if (doc == null) return false;

        foreach (var (key, value) in updates)
        {
            doc[key] = _db.Mapper.ToDocument(value);
        }

        doc["LastUpdated"] = DateTime.UtcNow;
        col.Update(doc);
        return true;
    }

    public IEnumerable<BsonDocument> QueryLore(string? query, string[]? tags, string? category, int limit = 5)
    {
        var col = _db.GetCollection("lore");
        var q = col.Query();
        
        if (!string.IsNullOrEmpty(query))
        {
            q = q.Where(Query.Or(
                Query.Contains("Title", query), 
                Query.Contains("Content", query),
                Query.Contains("Keywords", query)
            ));
        }
        
        if (tags != null && tags.Length > 0)
        {
            foreach (var tag in tags)
            {
                q = q.Where(Query.Contains("Tags", tag));
            }
        }
        
        if (!string.IsNullOrEmpty(category))
        {
            q = q.Where(Query.EQ("Category", category));
        }
        
        return q.Limit(limit).ToEnumerable();
    }

    public void LogEvent(Event @event)
    {
        var col = _db.GetCollection("events");
        col.Insert(_db.Mapper.ToDocument(@event));
    }

    public void Dispose() => _db.Dispose();
}
