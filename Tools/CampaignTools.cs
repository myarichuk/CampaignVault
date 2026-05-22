using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using LiteDB;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CampaignTools(CampaignRepository repository)
{
    [McpServerTool]
    [Description("Retrieve full details for a specific character by name or ID. Use this before narrating anything about a PC or important NPC.")]
    public async Task<object> GetCharacter(
        [Description("Name, slug, or partial match.")] string identifier)
    {
        var doc = repository.GetCharacter(identifier);
        if (doc == null) return new { error = "Character not found" };

        var data = MapBsonDocument(doc);
        var name = doc["Name"]?.AsString ?? "Unknown";
        var level = doc["ClassLevel"]?.AsString ?? "Unknown";
        var hp = doc["CurrentHp"]?.AsInt32 ?? 0;
        var maxHp = doc["MaxHp"]?.AsInt32 ?? 0;

        return new
        {
            data,
            summary = $"{name} ({level}). HP: {hp}/{maxHp}."
        };
    }

    [McpServerTool]
    [Description("Create or fully replace a character record. Use when a character is created or when you have a complete updated sheet.")]
    public async Task<object> UpsertCharacter(Character character)
    {
        repository.UpsertCharacter(character);
        return new 
        { 
            success = true, 
            data = character, 
            summary = $"Successfully saved character: {character.Name}" 
        };
    }

    [McpServerTool]
    [Description("Partial update to an existing character (preferred for most in-session changes: HP, status, notes, relationships).")]
    public async Task<object> UpdateCharacter(
        [Description("The character ID or name.")] string identifier, 
        [Description("Fields to merge/update (e.g. { \"currentHp\": 42, \"status\": [\"poisoned\"] })")] Dictionary<string, object> updates)
    {
        var success = repository.UpdateCharacter(identifier, updates);
        if (!success) return new { error = "Character not found" };
        
        return new 
        { 
            success = true, 
            summary = $"Updated character {identifier} with {updates.Count} changes." 
        };
    }

    [McpServerTool]
    [Description("Search lore entries by keywords, tags, or category. Returns the most relevant matches.")]
    public async Task<object> QueryLore(
        [Description("Free text or keywords")] string? query = null, 
        [Description("Tags to filter by")] string[]? tags = null, 
        [Description("Category (e.g. 'npc', 'location', 'item', 'plot')")] string? category = null, 
        [Description("Max results to return")] int limit = 5)
    {
        var results = repository.QueryLore(query, tags, category, limit).Select(MapBsonDocument).ToList();
        return new
        {
            data = results,
            summary = $"Found {results.Count} lore entries."
        };
    }

    [McpServerTool]
    [Description("Append an important in-game event to the session log. Call this for major beats the party should remember.")]
    public async Task<object> LogEvent(
        [Description("Short summary of the event")] string summary, 
        [Description("Type (e.g. 'combat', 'social', 'discovery')")] string type, 
        [Description("Arbitrary details object")] Dictionary<string, object>? details = null, 
        [Description("Names of involved characters")] string[]? involved = null)
    {
        var @event = new Event
        {
            Summary = summary,
            Type = type,
            Details = details,
            Involved = involved?.ToList() ?? []
        };
        repository.LogEvent(@event);
        return new 
        { 
            success = true, 
            eventId = @event.Id, 
            summary = $"Event logged: {summary}" 
        };
    }

    private static Dictionary<string, object?> MapBsonDocument(BsonDocument doc)
    {
        return doc.Keys.ToDictionary(k => k, k => MapBsonValue(doc[k]));
    }

    private static object? MapBsonValue(BsonValue value)
    {
        if (value.IsDocument) return MapBsonDocument(value.AsDocument);
        if (value.IsArray) return value.AsArray.Select(MapBsonValue).ToList();
        return value.RawValue;
    }
}
