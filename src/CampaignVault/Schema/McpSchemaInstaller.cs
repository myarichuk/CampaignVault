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
        return services.PostConfigure<McpServerOptions>(options =>
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

            // Install take_turn schema
            if (options.ToolCollection?.TryGetPrimitive("take_turn", out var takeTurnTool) == true)
            {
                var takeTurnSchema = TakeTurnSchemaBuilder.Build(jsonOptions);
                takeTurnTool.ProtocolTool.InputSchema = takeTurnSchema;
            }

            // Install world_build schema
            if (options.ToolCollection?.TryGetPrimitive("world_build", out var worldBuildTool) == true)
            {
                var worldBuildSchema = WorldBuildSchemaBuilder.Build(jsonOptions);
                worldBuildTool.ProtocolTool.InputSchema = worldBuildSchema;
            }

            // Strip write-guidance descriptions (race/feat/spell-DC template text) from
            // systemStats subtrees in every tool's reflection-generated OutputSchema — that
            // text only helps when constructing a systemStats payload, not when reading one
            // back in a response.
            if (options.ToolCollection is not null)
            {
                foreach (var tool in options.ToolCollection)
                {
                    if (tool.ProtocolTool.OutputSchema is { } outputSchema)
                    {
                        tool.ProtocolTool.OutputSchema = OutputSchemaTrimmer.StripSystemStatsDescriptions(outputSchema);
                    }
                }
            }

            // take_turn/start_session's reflection-derived OutputSchema is ~40KB/~18KB of pure
            // response-shape scaffolding (already stripped of write-guidance text above) that costs
            // real tools/list tokens on every session but has no accuracy payoff: the SDK only needs
            // Tool.OutputSchema to be non-null to populate StructuredContent (and let McpResponseCleaner
            // collapse Content down to the narrative summary) — it never validates the return value
            // against the schema's actual shape. Verified live: a bare {"type":"object"} stub still
            // produces a full StructuredContent and a collapsed Content on a real tool call. The model
            // reads the real response JSON on every call anyway, so the a-priori schema's only paying
            // job — informing the model in advance what fields to expect — is worth little relative to
            // its size for these two tools specifically (every other tool's OutputSchema is small enough
            // that the anticipatory value is worth keeping).
            using var minimalOutputSchemaDoc = JsonDocument.Parse("""{"type":"object"}""");
            var minimalOutputSchema = minimalOutputSchemaDoc.RootElement.Clone();
            foreach (var toolName in new[] { "take_turn", "start_session" })
            {
                if (options.ToolCollection?.TryGetPrimitive(toolName, out var tool) == true)
                {
                    tool.ProtocolTool.OutputSchema = minimalOutputSchema;
                }
            }
        });
    }
}
