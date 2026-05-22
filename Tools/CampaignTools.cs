using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CampaignTools(CampaignRepository repository)
{
    [McpServerTool]
    [Description("Retrieve full details for a specific character by name or ID. Use this before narrating anything about a PC or important NPC.")]
    public async Task<object> GetCharacter(
        [Description("Name, slug, or partial match.")] string identifier)
    {
        var character = await repository.GetCharacterAsync(identifier);
        if (character == null) return new { error = "Character not found" };

        return new
        {
            data = character,
            summary = $"{character.Name} ({character.ClassLevel}). HP: {character.CurrentHp}/{character.MaxHp}."
        };
    }

    [McpServerTool]
    [Description("Create or fully replace a character record. Use when a character is created or when you have a complete updated sheet.")]
    public async Task<object> UpsertCharacter(Character character)
    {
        await repository.UpsertCharacterAsync(character);
        return new 
        { 
            success = true, 
            data = character, 
            summary = $"Successfully saved character: {character.Name}" 
        };
    }

    [McpServerTool]
    [Description("Partial update to an existing character (preferred for most in-session changes: HP, status, notes, relationships, needs).")]
    public async Task<object> UpdateCharacter(
        [Description("The character ID or name.")] string identifier, 
        [Description("Fields to merge/update (e.g. { \"currentHp\": 42, \"needs\": { \"hunger\": 20 } })")] Dictionary<string, object> updates)
    {
        var success = await repository.UpdateCharacterAsync(identifier, updates);
        if (!success) return new { error = "Character not found" };
        
        return new 
        { 
            success = true, 
            summary = $"Updated character {identifier} with {updates.Count} changes." 
        };
    }

    [McpServerTool]
    [Description("Search lore entries by keywords, tags, or category. Supports fuzzy search.")]
    public async Task<object> QueryLore(
        [Description("Free text or keywords (fuzzy)")] string? query = null, 
        [Description("Tags to filter by")] string[]? tags = null, 
        [Description("Category (e.g. 'npc', 'location', 'item', 'plot')")] string? category = null, 
        [Description("Max results to return")] int limit = 5)
    {
        var results = (await repository.QueryLoreAsync(query, tags, category, limit)).ToList();
        return new
        {
            data = results,
            summary = $"Found {results.Count} lore entries."
        };
    }

    [McpServerTool]
    [Description("Create or fully replace a lore entry. Use this when you invent a new NPC, location, or historical fact.")]
    public async Task<object> UpsertLore(Lore lore)
    {
        await repository.UpsertLoreAsync(lore);
        return new 
        { 
            success = true, 
            data = lore, 
            summary = $"Successfully saved lore entry: {lore.Title}" 
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
        await repository.LogEventAsync(@event);
        return new 
        { 
            success = true, 
            eventId = @event.Id, 
            summary = $"Event logged: {summary}" 
        };
    }

    [McpServerTool]
    [Description("Retrieve recent in-game events. Use this to catch up on what happened in previous sessions.")]
    public async Task<object> QueryEvents(
        [Description("Keywords to search for in event summaries")] string? query = null, 
        [Description("Filter by event type (e.g. 'combat', 'social')")] string? type = null, 
        [Description("Max results to return")] int limit = 10)
    {
        var results = (await repository.QueryEventsAsync(query, type, limit)).ToList();
        return new
        {
            data = results,
            summary = $"Found {results.Count} recent events."
        };
    }
}
