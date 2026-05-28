using CampaignVault.Data;
using CampaignVault.Tools;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Embedded;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var dbPath = builder.Configuration["CAMPAIGN_DB_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "RavenData");
var bearerToken = builder.Configuration["BEARER_TOKEN"];

// RavenDB Embedded Setup
EmbeddedServer.Instance.StartServer(new ServerOptions
{
    DataDirectory = dbPath,
    ServerUrl = "http://127.0.0.1:0" // Use a random port
});
var documentStore = EmbeddedServer.Instance.GetDocumentStore("CampaignVault");
IndexCreation.CreateIndexes(typeof(Program).Assembly, documentStore);

// Services
builder.Services.AddSingleton<IDocumentStore>(documentStore);

// Simulation engine + default rules (extensible via additional AddSingleton<ISimulationRule, ...>)
builder.Services.AddSingleton<ISimulationRule, NeedsAccumulationRule>();
builder.Services.AddSingleton<ISimulationRule, RumorDecayRule>();
builder.Services.AddSingleton<ISimulationRule, ScheduleEvaluationRule>();
builder.Services.AddSingleton<IWorldSimulationEngine, DefaultSimulationEngine>();

// Behavioral synthesis (deterministic first, cheap & predictable)
builder.Services.AddSingleton<INpcBehaviorSynthesizer, DefaultBehaviorSynthesizer>();

builder.Services.AddSingleton<CampaignRepository>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("Mcp-Session-Id");
    });
});
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = "CampaignVault",
        Version = "0.2.0"
    };
})
.WithStdioServerTransport()
.WithHttpTransport(options =>
{
    options.Stateless = true;
})
.WithToolsFromAssembly();

var app = builder.Build();

app.UseCors();

// Optional Bearer/X-API-Key Auth Middleware
if (!string.IsNullOrEmpty(bearerToken))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/" || context.Request.Path == "/health")
        {
            await next();
            return;
        }

        var authorized = false;

        // Preferred: Authorization header (standard and more secure)
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) && 
            authHeader.ToString().Equals($"Bearer {bearerToken}", StringComparison.OrdinalIgnoreCase))
        {
            authorized = true;
        }
        // Alternative header
        else if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader) && 
                 apiKeyHeader.ToString().Equals(bearerToken, StringComparison.OrdinalIgnoreCase))
        {
            authorized = true;
        }
        // Query string fallback (for clients like Grok Web custom connectors that cannot set custom headers)
        // WARNING: Query parameters are logged in many places (server logs, proxies, browser history, etc.).
        // Only use this when header-based auth is not possible. Prefer headers whenever available.
        else
        {
            var queryToken = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["auth"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["bearer"].ToString();

            if (queryToken.Equals(bearerToken, StringComparison.OrdinalIgnoreCase))
            {
                authorized = true;
            }
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

app.MapMcp("/");
app.MapGet("/info", () => "D&D Campaign Vault MCP Server (RavenDB) is running.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Diagnostic Startup Info
app.Lifetime.ApplicationStarted.Register(() =>
{
    var server = app.Services.GetRequiredService<IServer>();
    var addressFeature = server.Features.Get<IServerAddressesFeature>();
    var addresses = addressFeature?.Addresses ?? new List<string>();

    var dbPathSetting = builder.Configuration["CAMPAIGN_DB_PATH"] ?? "RavenData (default)";
    var authEnabled = !string.IsNullOrEmpty(bearerToken);

    // Write to Error stream to avoid corrupting Stdio MCP transport
    Console.Error.WriteLine(@"
      ____                               _               __     __             _ _   
     / ___|__ _ _ __ ___  _ __   __ _(_) __ _ _ __   \ \   / /_ _ _   _| | |_ 
    | |   / _` | '_ ` _ \| '_ \ / _` | |/ _` | '_ \   \ \ / / _` | | | | | __|
    | |__| (_| | | | | | | |_) | (_| | | (_| | | | |   \ V / (_| | |_| | | |_ 
     \____\__,_|_| |_| |_| .__/ \__,_|_|\__, |_| |_|    \_/ \__,_|\__,_|_|\__|
                         |_|            |___/                                 
    ");
    Console.Error.WriteLine($"MCP Version: 0.2.0");
    Console.Error.WriteLine($"Database Path: {dbPathSetting}");
    Console.Error.WriteLine($"Auth Enabled: {authEnabled}");
    Console.Error.WriteLine("Listening on:");
    foreach (var address in addresses)
    {
        Console.Error.WriteLine($"  - {address}");
    }
    Console.Error.WriteLine("--------------------------------------------------");
});

app.Run();
