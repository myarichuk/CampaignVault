using System.Text.Json;
using ModelContextProtocol.Server;

namespace CampaignVault.Schema;

/// <summary>
/// Installs pre-built, tiered tool schemas via PostConfigure. The HTTP transport builds fresh
/// McpServerOptions for every session (every request when stateless, or whenever ConfigureSessionOptions
/// is set), so this PostConfigure runs per session: the schemas are built once and cached here.
/// </summary>
internal static class McpSchemaInstaller
{
    private static readonly JsonSerializerOptions SchemaJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ToolSchemaMode, JsonElement> TakeTurnSchemas = new();
    private static readonly Lazy<JsonElement> WorldBuildSchema = new(() => WorldBuildSchemaBuilder.Build(SchemaJsonOptions));
    private static readonly JsonElement MinimalOutputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    // Hand-written input schemas (3e). The reflection schema emits `"type":["string","null"],"default":null` for
    // every optional parameter and repeats each [Description]; these say the same thing in a fraction of the
    // chars, and the binder matches arguments by name regardless of the advertised schema. The method
    // signatures stay the source of truth for binding: HandWrittenSchemas_MatchTheirMethods pins the two.
    // end_session: SessionHandoff's reflection schema cost ~2.1k chars; the handoff's fields ride in the tool
    // description (full guide: lookup kind=help topic=sessions). recapText is deliberately not advertised: it
    // still binds as a deprecated alias for older prompts.
    internal static readonly IReadOnlyDictionary<string, JsonElement> HandWrittenSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["end_session"] = Parse("""
            {"type":"object","properties":{"campaignName":{"type":"string"},"handoff":{"type":"object"},"checkpoint":{"type":"boolean"}},"required":["campaignName","handoff"]}
            """),
        ["lookup"] = Parse("""
            {"type":"object","properties":{"kind":{"type":"string","enum":["handbook","spells","creatures","items","item_tags","level_up","commit_schema","help"]},"campaignName":{"type":"string","description":"Not needed for commit_schema/help."},"type":{"type":"string","description":"commit_schema: one $type."},"topic":{"type":"string","description":"help: topic."},"className":{"type":"string","description":"spells, level_up."},"level":{"type":"integer","description":"spells: 0 = cantrip."},"query":{"type":"string","description":"Name search."},"category":{"type":"string","description":"items; commit_schema: Combat|Narrative|World|PlotThread."},"tag":{"type":"string","description":"items."},"levelMin":{"type":"integer","description":"creatures."},"levelMax":{"type":"integer","description":"creatures."},"characterId":{"type":"string","description":"level_up."},"offset":{"type":"integer"},"limit":{"type":"integer","description":"default 40, max 100."}},"required":["kind"]}
            """),
        ["combat"] = Parse("""
            {"type":"object","properties":{"campaignName":{"type":"string"},"action":{"type":"string","enum":["start","next","end","status"]},"locationId":{"type":"string","description":"start."},"combatantIds":{"type":"array","items":{"type":"string"},"description":"start: character IDs."},"overwriteActive":{"type":"boolean","description":"start: replace an active combat."},"expectedActiveTurnId":{"type":"string","description":"next: fail unless this turn is active."}},"required":["campaignName","action"]}
            """),
        ["advance_world"] = Parse("""
            {"type":"object","properties":{"campaignName":{"type":"string"},"narrative":{"type":"string","description":"The rest, travel or downtime."},"hours":{"type":"integer","description":"From now, e.g. 8 for a night."},"days":{"type":"integer","description":"Whole days; with resultingHour."},"resultingHour":{"type":"integer","description":"0-23, with days."},"partyLocationId":{"type":"string","description":"Enables encounter checks; omit for a safe skip."}},"required":["campaignName","narrative"]}
            """),
        ["create_campaign"] = Parse("""
            {"type":"object","properties":{"name":{"type":"string","description":"Slug; spaces become hyphens."},"initialSystem":{"type":"string","description":"dnd5e, pf2e, narrative (plugins may add more); locked."},"displayName":{"type":"string"},"narrativeFocus":{"type":"array","items":{"type":"string"},"description":"Story tags, e.g. political intrigue."},"loreEpoch":{"type":"string","description":"Era name."},"loreYear":{"type":"integer","description":"Default 1492."},"loreMonth":{"type":"integer","description":"1-12."},"loreDay":{"type":"integer","description":"1-30."},"loreHour":{"type":"integer","description":"0-23, default 6."}},"required":["name","initialSystem"]}
            """),
        ["start_session"] = Parse("""
            {"type":"object","properties":{"campaignName":{"type":"string"},"title":{"type":"string"},"partyLocationId":{"type":"string","description":"Defaults to the PC's location."},"includeScene":{"type":"boolean","description":"Default true: the opening scene in 'scene'."}},"required":["campaignName"]}
            """),
        ["get_entity"] = Parse("""
            {"type":"object","properties":{"entityId":{"type":"string","description":"e.g. chars/valen, locations/rusty-nail; 'plot-threads' lists threads."},"campaignName":{"type":"string"},"partyPresent":{"type":"boolean","description":"locations: the party is here."},"fullDescription":{"type":"boolean","description":"locations: untruncated description."},"includeCombatDetail":{"type":"boolean","description":"chars: class feature rule text."}},"required":["entityId","campaignName"]}
            """),
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static IServiceCollection AddCampaignVaultToolSchemas(this IServiceCollection services)
    {
        services.AddOptions<McpServerOptions>().PostConfigure<IConfiguration>((options, configuration) =>
        {
            var mode = ResolveMode(configuration);

            // Install take_turn schema
            if (options.ToolCollection?.TryGetPrimitive("take_turn", out var takeTurnTool) == true)
            {
                takeTurnTool.ProtocolTool.InputSchema =
                    TakeTurnSchemas.GetOrAdd(mode, m => TakeTurnSchemaBuilder.Build(SchemaJsonOptions, m));
            }

            // Install world_build schema
            if (options.ToolCollection?.TryGetPrimitive("world_build", out var worldBuildTool) == true)
            {
                worldBuildTool.ProtocolTool.InputSchema = WorldBuildSchema.Value;
            }

            foreach (var (name, schema) in HandWrittenSchemas)
            {
                if (options.ToolCollection?.TryGetPrimitive(name, out var tool) == true)
                {
                    tool.ProtocolTool.InputSchema = schema;
                }
            }

            // Reflection-derived OutputSchemas are pure response-shape scaffolding (~17k chars across
            // all tools, get_config alone ~5k) that costs tools/list tokens on every session with no
            // accuracy payoff: the SDK only needs Tool.OutputSchema to be non-null to populate
            // StructuredContent (and let McpResponseCleaner collapse Content down to the narrative
            // summary) — it never validates the return value against the schema's shape. Verified
            // live: a bare {"type":"object"} stub still produces full StructuredContent and a collapsed
            // Content. The model reads the real response JSON on every call anyway.
            if (options.ToolCollection is not null)
            {
                foreach (var tool in options.ToolCollection)
                {
                    if (tool.ProtocolTool.OutputSchema is not null)
                    {
                        tool.ProtocolTool.OutputSchema = MinimalOutputSchema;
                    }
                }
            }
        });

        return services;
    }

    internal static ToolSchemaMode ResolveMode(IConfiguration configuration) =>
        ParseMode(configuration["CampaignVault:ToolSchemaMode"]);

    internal static ToolSchemaMode ParseMode(string? value) =>
        Enum.TryParse<ToolSchemaMode>(value, ignoreCase: true, out var mode)
            ? mode
            : ToolSchemaMode.Stub;
}
