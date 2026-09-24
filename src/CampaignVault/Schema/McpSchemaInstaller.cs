using System.Text.Json;
using ModelContextProtocol.Server;

namespace CampaignVault.Schema;

/// <summary>
/// Installs pre-built, tiered tool schemas at startup, replacing per-request generation.
/// This runs once via PostConfigure and never recomputes—dramatically reducing tools/list overhead.
/// </summary>
internal static class McpSchemaInstaller
{
    public static IServiceCollection AddCampaignVaultToolSchemas(this IServiceCollection services)
    {
        services.AddOptions<McpServerOptions>().PostConfigure<IConfiguration>((options, configuration) =>
        {
            var mode = ResolveMode(configuration);
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            // Install take_turn schema
            if (options.ToolCollection?.TryGetPrimitive("take_turn", out var takeTurnTool) == true)
            {
                var takeTurnSchema = TakeTurnSchemaBuilder.Build(jsonOptions, mode);
                takeTurnTool.ProtocolTool.InputSchema = takeTurnSchema;
            }

            // Install world_build schema
            if (options.ToolCollection?.TryGetPrimitive("world_build", out var worldBuildTool) == true)
            {
                var worldBuildSchema = WorldBuildSchemaBuilder.Build(jsonOptions);
                worldBuildTool.ProtocolTool.InputSchema = worldBuildSchema;
            }

            // Reflection-derived OutputSchemas are pure response-shape scaffolding (~17k chars across
            // all tools, get_config alone ~5k) that costs tools/list tokens on every session with no
            // accuracy payoff: the SDK only needs Tool.OutputSchema to be non-null to populate
            // StructuredContent (and let McpResponseCleaner collapse Content down to the narrative
            // summary) — it never validates the return value against the schema's shape. Verified
            // live: a bare {"type":"object"} stub still produces full StructuredContent and a collapsed
            // Content. The model reads the real response JSON on every call anyway.
            using var minimalOutputSchemaDoc = JsonDocument.Parse("""{"type":"object"}""");
            var minimalOutputSchema = minimalOutputSchemaDoc.RootElement.Clone();
            if (options.ToolCollection is not null)
            {
                foreach (var tool in options.ToolCollection)
                {
                    if (tool.ProtocolTool.OutputSchema is not null)
                    {
                        tool.ProtocolTool.OutputSchema = minimalOutputSchema;
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
