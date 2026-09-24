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
