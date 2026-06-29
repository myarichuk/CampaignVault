using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using CampaignVault.Authoring.Services;
using CampaignVault.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CampaignVault.Authoring.Vault.Canonical;

/// <summary>
/// Deterministic markdown ↔ JSON conversion for vault entity sync.
/// Canonical hashes are SHA-256 of normalized entity JSON (camelCase) after stripping
/// <c>campaignName</c>, <c>lastUpdated</c>, and event <c>timestamp</c> — metadata set
/// on push, not trusted from files alone. Body-mapped fields (notes, description, etc.)
/// are included in the JSON hash. Canonical markdown ends with a trailing <c>\n</c>.
/// </summary>
public sealed class EntityCanonicalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    // Includes zero/false/empty-list values so templates show all meaningful fields.
    private static readonly ISerializer TemplateSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly WorkspaceParser _parser = new();

    public string MarkdownToJson(string entityType, string markdown)
    {
        var model = ParseToModel(entityType, markdown);
        ClearSyncExcludedFields(model);
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    public string JsonToMarkdown(string entityType, string json)
    {
        var model = DeserializeModel(entityType, json)
                    ?? throw new VaultException($"Could not deserialize {entityType} entity from JSON.");

        return BuildCanonicalMarkdown(entityType, model);
    }

    public string ComputeCanonicalHash(string entityType, string markdown)
    {
        var json = MarkdownToJson(entityType, markdown);
        return ComputeCanonicalHashFromJson(entityType, json);
    }

    public string ComputeCanonicalHashFromJson(string entityType, string json)
    {
        var model = DeserializeModel(entityType, json)
                    ?? throw new VaultException($"Could not deserialize {entityType} entity from JSON.");
        ClearSyncExcludedFields(model);
        return VaultContentHash.Compute(JsonSerializer.Serialize(model, JsonOptions));
    }

    public string MarkdownToPushJson(string entityType, string markdown, string campaignName)
    {
        var model = ParseToModel(entityType, markdown);
        ApplyPushMetadata(model, campaignName);
        return JsonSerializer.Serialize(model, JsonOptions);
    }

    private static void ApplyPushMetadata(object model, string campaignName)
    {
        switch (model)
        {
            case Character c:
                c.CampaignName = campaignName;
                c.LastUpdated = DateTime.UtcNow;
                break;
            case Location l:
                l.CampaignName = campaignName;
                l.LastUpdated = DateTime.UtcNow;
                break;
            case Quest q:
                q.CampaignName = campaignName;
                q.LastUpdated = DateTime.UtcNow;
                break;
            case Faction f:
                f.CampaignName = campaignName;
                f.LastUpdated = DateTime.UtcNow;
                break;
            case Lore lore:
                lore.CampaignName = campaignName;
                lore.LastUpdated = DateTime.UtcNow;
                break;
            case Rumor r:
                r.CampaignName = campaignName;
                r.LastUpdated = DateTime.UtcNow;
                break;
            case Event e:
                e.CampaignName = campaignName;
                e.Timestamp = DateTime.UtcNow;
                break;
            case Item i:
                i.CampaignName = campaignName;
                i.LastUpdated = DateTime.UtcNow;
                break;
        }
    }

    public string NormalizeToCanonicalMarkdown(string entityType, string markdown)
    {
        var json = MarkdownToJson(entityType, markdown);
        return JsonToMarkdown(entityType, json);
    }

    private object ParseToModel(string entityType, string markdown) =>
        entityType switch
        {
            "character" => _parser.ParseCharacter(markdown),
            "location" => _parser.ParseLocation(markdown),
            "quest" => _parser.ParseQuest(markdown),
            "faction" => _parser.ParseFaction(markdown),
            "lore" => _parser.ParseLore(markdown),
            "rumor" => _parser.ParseRumor(markdown),
            "event" => _parser.ParseEvent(markdown),
            "item" => _parser.ParseItem(markdown),
            _ => throw new VaultException($"Unsupported entity type '{entityType}'.")
        };

    private object? DeserializeModel(string entityType, string json) =>
        entityType switch
        {
            "character" => JsonSerializer.Deserialize<Character>(json, JsonOptions),
            "location" => JsonSerializer.Deserialize<Location>(json, JsonOptions),
            "quest" => JsonSerializer.Deserialize<Quest>(json, JsonOptions),
            "faction" => JsonSerializer.Deserialize<Faction>(json, JsonOptions),
            "lore" => JsonSerializer.Deserialize<Lore>(json, JsonOptions),
            "rumor" => JsonSerializer.Deserialize<Rumor>(json, JsonOptions),
            "event" => JsonSerializer.Deserialize<Event>(json, JsonOptions),
            "item" => JsonSerializer.Deserialize<Item>(json, JsonOptions),
            _ => throw new VaultException($"Unsupported entity type '{entityType}'.")
        };

    private string BuildCanonicalMarkdown(string entityType, object model)
    {
        var (frontmatter, body) = entityType switch
        {
            "character" => BuildCharacter((Character)model),
            "location" => BuildLocation((Location)model),
            "quest" => BuildQuest((Quest)model),
            "faction" => BuildFaction((Faction)model),
            "lore" => BuildLore((Lore)model),
            "rumor" => BuildRumor((Rumor)model),
            "event" => BuildEvent((Event)model),
            "item" => BuildItem((Item)model),
            _ => throw new VaultException($"Unsupported entity type '{entityType}'.")
        };

        var yaml = YamlSerializer.Serialize(frontmatter);
        var markdown = $"---\n{yaml}---\n\n{body}".ReplaceLineEndings("\n");
        if (!markdown.EndsWith('\n'))
            markdown += "\n";
        return markdown;
    }

    private static void ClearSyncExcludedFields(object model)
    {
        switch (model)
        {
            case Character c:
                c.CampaignName = null;
                c.LastUpdated = default;
                break;
            case Location l:
                l.CampaignName = null;
                l.LastUpdated = default;
                break;
            case Quest q:
                q.CampaignName = null;
                q.LastUpdated = default;
                break;
            case Faction f:
                f.CampaignName = null;
                f.LastUpdated = default;
                break;
            case Lore lore:
                lore.CampaignName = null;
                lore.LastUpdated = default;
                break;
            case Rumor r:
                r.CampaignName = null;
                r.LastUpdated = default;
                break;
            case Event e:
                e.CampaignName = null;
                e.Timestamp = default;
                break;
            case Item i:
                i.CampaignName = null;
                i.LastUpdated = default;
                break;
        }
    }

    private static (object frontmatter, string body) BuildCharacter(Character c) =>
        (new Character
        {
            Id = c.Id,
            Name = c.Name,
            ClassLevel = c.ClassLevel,
            CurrentHp = c.CurrentHp,
            MaxHp = c.MaxHp,
            DistinctiveFeatures = c.DistinctiveFeatures,
            CurrentAppearance = c.CurrentAppearance,
            VisualTags = c.VisualTags,
            KeepAlive = c.KeepAlive,
            IsPc = c.IsPc,
            IsPartyCompanion = c.IsPartyCompanion,
            Schedule = c.Schedule,
            CurrentLocationId = c.CurrentLocationId,
            CurrentActivity = c.CurrentActivity,
            Psychology = c.Psychology,
            Social = c.Social,
            Needs = c.Needs,
            SystemStats = c.SystemStats
        }, c.Notes ?? string.Empty);

    private static (object frontmatter, string body) BuildLocation(Location l) =>
        (new Location
        {
            Id = l.Id,
            Name = l.Name,
            Type = l.Type,
            ParentLocationId = l.ParentLocationId,
            Exits = l.Exits,
            PointsOfInterest = l.PointsOfInterest,
            PointOfInterestDetails = l.PointOfInterestDetails,
            AmbientCrowd = l.AmbientCrowd,
            LastVisitedDay = l.LastVisitedDay,
            RecentlyDeparted = l.RecentlyDeparted,
            Metadata = l.Metadata,
            CurrentState = l.CurrentState,
            VisualTags = l.VisualTags,
            DistinctiveFeatures = l.DistinctiveFeatures,
            ControllingFactionId = l.ControllingFactionId,
            DangerModifier = l.DangerModifier
        }, l.Description ?? string.Empty);

    private static (object frontmatter, string body) BuildQuest(Quest q) =>
        (new Quest
        {
            Id = q.Id,
            Title = q.Title,
            GiverId = q.GiverId,
            Objectives = q.Objectives,
            OverallState = q.OverallState,
            Category = q.Category,
            Urgency = q.Urgency,
            RelatedLocationIds = q.RelatedLocationIds,
            RelatedFactionIds = q.RelatedFactionIds,
            VisibleToCharacterIds = q.VisibleToCharacterIds,
            DeadlineDay = q.DeadlineDay,
            LastUpdatedDay = q.LastUpdatedDay
        }, q.DmNotes ?? string.Empty);

    private static (object frontmatter, string body) BuildFaction(Faction f) =>
        (new Faction
        {
            Id = f.Id,
            Name = f.Name,
            FactionType = f.FactionType,
            ControllingTerritory = f.ControllingTerritory,
            TerritoryLocationIds = f.TerritoryLocationIds,
            KnownLeaderIds = f.KnownLeaderIds,
            InfluenceLevel = f.InfluenceLevel,
            EnemyFactionIds = f.EnemyFactionIds,
            StanceToward = f.StanceToward,
            EconomicDemand = f.EconomicDemand,
            Metadata = f.Metadata
        }, f.Description ?? string.Empty);

    private static (object frontmatter, string body) BuildLore(Lore l) =>
        (new Lore
        {
            Id = l.Id,
            Title = l.Title,
            Tags = l.Tags,
            Keywords = l.Keywords,
            Category = l.Category
        }, l.Content ?? string.Empty);

    private static (object frontmatter, string body) BuildRumor(Rumor r) =>
        (new Rumor
        {
            Id = r.Id,
            RegionLocationId = r.RegionLocationId,
            Subject = r.Subject,
            State = r.State,
            TruthValue = r.TruthValue,
            DayCreated = r.DayCreated,
            LastStateChangeDay = r.LastStateChangeDay
        }, r.CurrentText ?? string.Empty);

    private static (object frontmatter, string body) BuildEvent(Event e) =>
        (new Event
        {
            Id = e.Id,
            Timestamp = e.Timestamp,
            DayLogged = e.DayLogged,
            SessionId = e.SessionId,
            Category = e.Category,
            Details = e.Details,
            Involved = e.Involved,
            EmotionalBeat = e.EmotionalBeat,
            RelatedEntityId = e.RelatedEntityId
        }, e.Summary ?? string.Empty);

    private static (object frontmatter, string body) BuildItem(Item i) =>
        (new Item
        {
            Id = i.Id,
            Name = i.Name,
            Description = string.Empty, // body is separate
            HolderId = i.HolderId,
            CurrentState = i.CurrentState,
            DistinctiveFeatures = i.DistinctiveFeatures,
            CoreCategory = i.CoreCategory,
            Tags = i.Tags,
            Properties = i.Properties
        }, i.Description ?? string.Empty);

    /// <summary>
    /// Returns a canonical markdown template for a new entity with all meaningful fields populated
    /// at sensible defaults. Uses the same field whitelist as the sync serializer, guaranteeing
    /// round-trip fidelity on first save.
    /// </summary>
    public string GetBlankTemplate(string entityType, string id, string name)
    {
        var model = CreateBlankModel(entityType, id, name);
        var (frontmatter, body) = entityType switch
        {
            "character" => BuildCharacter((Character)model),
            "location"  => BuildLocation((Location)model),
            "quest"     => BuildQuest((Quest)model),
            "faction"   => BuildFaction((Faction)model),
            "lore"      => BuildLore((Lore)model),
            "rumor"     => BuildRumor((Rumor)model),
            "event"     => BuildEvent((Event)model),
            "item"      => BuildItem((Item)model),
            _ => throw new VaultException($"Unsupported entity type '{entityType}'.")
        };

        var yaml = TemplateSerializer.Serialize(frontmatter);
        var markdown = $"---\n{yaml}---\n\n{body}".ReplaceLineEndings("\n");
        if (!markdown.EndsWith('\n'))
            markdown += "\n";
        return markdown;
    }

    private static object CreateBlankModel(string entityType, string id, string name) => entityType switch
    {
        "character" => new Character
        {
            Id = id, Name = name, CurrentHp = 10, MaxHp = 10,
            Notes = "Notes and description here."
        },
        "location" => new Location
        {
            Id = id, Name = name, Type = LocationType.Building,
            Description = "Description of the location."
        },
        "quest" => new Quest
        {
            Id = id, Title = name, OverallState = QuestState.Open, Urgency = QuestUrgency.Normal,
            DmNotes = "DM notes for this quest."
        },
        "faction" => new Faction
        {
            Id = id, Name = name, FactionType = FactionType.Guild, InfluenceLevel = 50,
            Description = "Description of the faction."
        },
        "lore" => new Lore
        {
            Id = id, Title = name, Category = "General",
            Content = "Lore content goes here."
        },
        "rumor" => new Rumor
        {
            Id = id, Subject = name, State = RumorState.Nascent, TruthValue = RumorTruth.Unknown,
            CurrentText = "Current rumor text."
        },
        "event" => new Event
        {
            Id = id, Category = EventCategory.Discovery,
            Summary = "Event summary."
        },
        "item" => new Item
        {
            Id = id, Name = name, CoreCategory = ItemCategory.Other, HolderId = "",
            Description = "Item description and details."
        },
        _ => throw new VaultException($"Unsupported entity type '{entityType}'.")
    };
}