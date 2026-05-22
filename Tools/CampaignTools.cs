using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Raven.Client.Exceptions;

namespace CampaignVault.Tools;

[McpServerToolType]
public class CampaignTools(CampaignRepository repository)
{
    [McpServerTool]
    [Description("Retrieve authoritative details for a character. Use this BEFORE describing an NPC or PC to ensure stats and status are correct. Supports fuzzy name matching.")]
    public async Task<object> GetCharacter(
        [Description("The unique ID (e.g., 'npcs/gandalf'), the full Name, or a partial/misspelled name of the character.")] string identifier)
    {
        var character = await repository.GetCharacterAsync(identifier);
        if (character == null) 
        {
            return new { 
                error = "CharacterNotFound", 
                message = $"Could not find a character matching '{identifier}'. Use 'upsert_character' to create them if they are new." 
            };
        }

        return new
        {
            data = character,
            summary = $"{character.Name} ({character.ClassLevel ?? "Unknown Level"}). HP: {character.CurrentHp}/{character.MaxHp}. Status: {string.Join(", ", character.Status)}. Needs: {string.Join(", ", character.Needs.Select(n => $"{n.Key}:{n.Value}"))}."
        };
    }

    [McpServerTool]
    [Description("Create a new character or fully overwrite an existing one. Use this for initial character creation or major sheet updates.")]
    public async Task<object> UpsertCharacter(
        [Description("The full character object. Ensure 'Id' follows 'npcs/name' or 'pcs/name' format.")] Character character)
    {
        try 
        {
            await repository.UpsertCharacterAsync(character);
            return new 
            { 
                success = true, 
                data = character, 
                summary = $"Authoritative record for {character.Name} has been saved." 
            };
        }
        catch (ConcurrencyException)
        {
            return new { 
                error = "StateDriftConflict", 
                message = "The character was modified by another process (or a previous tool call) while you were thinking. Call 'get_character' to refresh your context before trying again." 
            };
        }
    }

    [McpServerTool]
    [Description("Update specific fields of a character (HP, status, notes, needs). Preferred for in-session changes.")]
    public async Task<object> UpdateCharacter(
        [Description("The unique ID or full Name of the character.")] string identifier, 
        [Description("Key-value pairs to update. Supported keys: 'currentHp', 'maxHp', 'notes', 'needs' (dictionary).")] Dictionary<string, object> updates)
    {
        try 
        {
            var success = await repository.UpdateCharacterAsync(identifier, updates);
            if (!success) 
            {
                return new { 
                    error = "CharacterNotFound", 
                    message = $"Update failed because character '{identifier}' does not exist." 
                };
            }
            
            return new 
            { 
                success = true, 
                summary = $"Applied {updates.Count} changes to {identifier}. Authoritative state updated." 
            };
        }
        catch (ConcurrencyException)
        {
            return new { 
                error = "StateDriftConflict", 
                message = "The character's state has changed since you last loaded it. Call 'get_character' to sync your context before re-applying updates." 
            };
        }
    }

    [McpServerTool]
    [Description("Search for campaign world information. Supports fuzzy matching for typos.")]
    public async Task<object> QueryLore(
        [Description("Search term (e.g., 'Sauron', 'Gondor'). Fuzzy matching is enabled, so slight typos are okay.")] string? query = null, 
        [Description("Filter by tags (e.g., ['location', 'faction']).")] string[]? tags = null, 
        [Description("Category filter (e.g., 'npc', 'history', 'item').")] string? category = null, 
        [Description("Maximum results to return. Default is 5.")] int limit = 5)
    {
        var results = (await repository.QueryLoreAsync(query, tags, category, limit)).ToList();
        return new
        {
            data = results,
            summary = $"Retrieved {results.Count} matching lore entries from the Vault."
        };
    }

    [McpServerTool]
    [Description("Create or update a lore entry (NPC backgrounds, location details, historical facts).")]
    public async Task<object> UpsertLore(
        [Description("The lore object. 'Id' should follow 'lore/slug' format. 'Title' and 'Content' are required.")] Lore lore)
    {
        try 
        {
            await repository.UpsertLoreAsync(lore);
            return new 
            { 
                success = true, 
                data = lore, 
                summary = $"Lore entry '{lore.Title}' is now part of the campaign's permanent record." 
            };
        }
        catch (ConcurrencyException)
        {
            return new { 
                error = "StateDriftConflict", 
                message = "This lore entry was updated while you were generating content. Call 'query_lore' to refresh your knowledge." 
            };
        }
    }

    [McpServerTool]
    [Description("Log a significant campaign event. Use this after major scenes, combats, or social breakthroughs.")]
    public async Task<object> LogEvent(
        [Description("A concise summary of what happened.")] string summary, 
        [Description("Type of event: 'combat', 'social', 'exploration', 'milestone'.")] string type, 
        [Description("Optional extra details for the history log.")] Dictionary<string, object>? details = null, 
        [Description("Names of characters or NPCs involved in this event.")] string[]? involved = null)
    {
        var @event = new Event
        {
            Id = "events/" + Guid.NewGuid().ToString(),
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
            summary = "The event has been etched into the campaign history." 
        };
    }

    [McpServerTool]
    [Description("Catch up on campaign history. Use this at the start of a session or when recalling past deeds.")]
    public async Task<object> QueryEvents(
        [Description("Search for keywords in past event summaries.")] string? query = null, 
        [Description("Filter by event type (e.g., 'combat').")] string? type = null, 
        [Description("Number of recent events to retrieve. Default is 10.")] int limit = 10)
    {
        var results = (await repository.QueryEventsAsync(query, type, limit)).ToList();
        return new
        {
            data = results,
            summary = $"Found {results.Count} events in the campaign archives."
        };
    }
}
