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

    Console.WriteLine(@"
      ____                               _               __     __             _ _   
     / ___|__ _ _ __ ___  _ __   __ _(_) __ _ _ __   \ \   / /_ _ _   _| | |_ 
    | |   / _` | '_ ` _ \| '_ \ / _` | |/ _` | '_ \   \ \ / / _` | | | | | __|
    | |__| (_| | | | | | | |_) | (_| | | (_| | | | |   \ V / (_| | |_| | | |_ 
     \____\__,_|_| |_| |_| .__/ \__,_|_|\__, |_| |_|    \_/ \__,_|\__,_|_|\__|
                         |_|            |___/                                 
    ");
    Console.WriteLine($"MCP Version: 0.2.0");
    Console.WriteLine($"Database Path: {dbPathSetting}");
    Console.WriteLine($"Auth Enabled: {authEnabled}");
    Console.WriteLine("Listening on:");
    foreach (var address in addresses)
    {
        Console.WriteLine($"  - {address}");
    }
    Console.WriteLine("--------------------------------------------------");
});

app.Run();
