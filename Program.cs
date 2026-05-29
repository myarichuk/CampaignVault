using CampaignVault.Data;
using CampaignVault.Tools;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Embedded;

using JsonSanitizer = CampaignVault.Data.JsonSanitizer; // for brevity in the listener

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

// Configuration
var dbPath = builder.Configuration["CAMPAIGN_DB_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "RavenData");

// SECURITY: Read bearer token *only* from the explicit environment variable.
// This prevents accidental leakage via appsettings.json, user secrets, command line, etc.
// Auth is enabled only when the variable is present and non-empty (same behavior as before).
var bearerToken = Environment.GetEnvironmentVariable("BEARER_TOKEN");

// RavenDB Embedded Setup
EmbeddedServer.Instance.StartServer(new ServerOptions
{
    DataDirectory = dbPath,
    ServerUrl = "http://127.0.0.1:0" // Use a random port
});
var documentStore = EmbeddedServer.Instance.GetDocumentStore("CampaignVault");
IndexCreation.CreateIndexes(typeof(Program).Assembly, documentStore);

// Universal sanitizing listener on the Raven persistence boundary.
// Any entity about to be stored (from tools, simulation, tests, direct session use, etc.)
// is sanitized here so that Dictionary<string, object> fields (Metadata, Properties, Details)
// never contain System.Text.Json.JsonElement instances when they hit Raven's (Newtonsoft) serializer.
// This is the "universal listener" layer. See also JsonSanitizer.cs + final guards in CampaignTools.
documentStore.OnBeforeStore += (_, args) =>
{
    if (args.Entity is not null)
    {
        JsonSanitizer.Sanitize(args.Entity);
    }
};

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

// CORS configuration (Issue #16 from code review)
// - Default (or "*"): AllowAnyOrigin (current behavior, convenient for local MCP + LLM clients)
// - Otherwise: comma-separated list of allowed origins (e.g. "https://app.example.com,https://dm.example.com")
// Set via CORS_ALLOWED_ORIGINS environment variable for production deployments.
var corsOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (string.IsNullOrWhiteSpace(corsOrigins) || corsOrigins.Trim() == "*")
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("Mcp-Session-Id");
        }
        else
        {
            var origins = corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .WithExposedHeaders("Mcp-Session-Id");
        }
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
// (defined at the bottom of this file for clarity)

var app = builder.Build();

app.UseCors();

// Timing-safe comparison for bearer tokens (prevents timing side-channel attacks).
// Tokens are compared exactly (case-sensitive) per security best practice.
static bool TimingSafeEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
    var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
}

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
            TimingSafeEquals(authHeader.ToString(), $"Bearer {bearerToken}"))
        {
            authorized = true;
        }
        // Alternative header (exact match)
        else if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader) &&
                 TimingSafeEquals(apiKeyHeader.ToString(), bearerToken))
        {
            authorized = true;
        }
        // Query string fallback (for clients like Grok Web custom connectors that cannot set custom headers)
        // SECURITY NOTE: Query parameters are logged in many places (server logs, proxies, browser history, etc.).
        // Only use this when header-based auth is not possible. Prefer headers whenever available.
        // Tokens are case-sensitive (exact match).
        else
        {
            var queryToken = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["auth"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["bearer"].ToString();

            if (TimingSafeEquals(queryToken, bearerToken))
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
