using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        });
    }
}
