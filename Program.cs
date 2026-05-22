using CampaignVault.Data;
using CampaignVault.Tools;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var dbPath = builder.Configuration["CAMPAIGN_DB_PATH"] ?? "campaign.db";
var bearerToken = builder.Configuration["BEARER_TOKEN"];

// Services
builder.Services.AddSingleton(new CampaignRepository(dbPath));
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = "CampaignVault",
        Version = "0.1.0"
    };
})
.WithHttpTransport()
.WithToolsFromAssembly();

var app = builder.Build();

// Optional Bearer/X-API-Key Auth Middleware
if (!string.IsNullOrEmpty(bearerToken))
{
    app.Use(async (context, next) =>
    {
        // Skip auth for root/health endpoints if needed, 
        // but for a private prototype, we can protect everything except GET /
        if (context.Request.Path == "/" || context.Request.Path == "/health")
        {
            await next();
            return;
        }

        var authorized = false;
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) && 
            authHeader.ToString().Equals($"Bearer {bearerToken}", StringComparison.OrdinalIgnoreCase))
        {
            authorized = true;
        }
        else if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader) && 
                 apiKeyHeader.ToString().Equals(bearerToken, StringComparison.OrdinalIgnoreCase))
        {
            authorized = true;
        }

        if (!authorized)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
        await next();
    });
}

app.MapMcp("mcp");
app.MapGet("/", () => "D&D Campaign Vault MCP Server is running.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
