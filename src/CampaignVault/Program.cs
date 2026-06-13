using Autofac;
using Autofac.Extensions.DependencyInjection;
using CampaignVault.Data;
using CampaignVault.Tools;
using CampaignVault.Middleware;
using CampaignVault.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Embedded;

using JsonSanitizer = CampaignVault.Data.JsonSanitizer; // for brevity in the listener

var mcpPort = int.TryParse(Environment.GetEnvironmentVariable("MCP_PORT"), out var configuredMcpPort)
    ? configuredMcpPort
    : 5275;
var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("GRPC_PORT"), out var configuredGrpcPort)
    ? configuredGrpcPort
    : 50051;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.PreferHostingUrls(false);
builder.WebHost.ConfigureKestrel(options =>
{
    // MCP + HTTP health/info
    options.ListenLocalhost(mcpPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
    });

    // Dedicated gRPC sync channel for the authoring tool — gRPC requires HTTP/2 only
    options.ListenLocalhost(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());

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
var documentStore = RavenStartup.Initialize(dbPath);

// Services
builder.Services.AddSingleton(documentStore);

builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
{
    containerBuilder.RegisterModule<CampaignVault.AutofacModules.SimulationModule>();
    containerBuilder.RegisterModule<CampaignVault.AutofacModules.RulesetsModule>();
    containerBuilder.RegisterModule<CampaignVault.AutofacModules.PressureModule>();
    containerBuilder.RegisterModule<CampaignVault.AutofacModules.InitiativeModule>();
    containerBuilder.RegisterModule<CampaignVault.AutofacModules.CampaignCoreModule>();
});

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
var enableStdioTransport = string.Equals(
    Environment.GetEnvironmentVariable("MCP_STDIO"),
    "1",
    StringComparison.OrdinalIgnoreCase);

var mcpServerBuilder = builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation
    {
        Name = "CampaignVault",
        Version = "0.2.0"
    };
});

if (enableStdioTransport)
{
    mcpServerBuilder.WithStdioServerTransport();
}

mcpServerBuilder
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithToolsFromAssembly()
    .WithRequestFilters(McpToolErrorFilter.Register);

builder.Services.AddGrpc();

var app = builder.Build();

app.UseCors();

app.UseMiddleware<McpNormalizationMiddleware>();

// Optional Bearer/X-API-Key Auth Middleware
if (!string.IsNullOrEmpty(bearerToken))
{
    app.UseMiddleware<AuthMiddleware>(bearerToken);
}

// Bind MCP + HTTP utility endpoints exclusively to the MCP listener port.
// Do not use RequireHost() — Grok Web and other MCP clients often send Host headers
// without a port suffix (e.g. "localhost"), which still 404s with *:port patterns.
app.MapMcp("/").RequireLocalPort(mcpPort);
app.MapGet("/info", () => "D&D Campaign Vault MCP Server (RavenDB) is running.")
   .RequireLocalPort(mcpPort);
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .RequireLocalPort(mcpPort);

// Bind gRPC sync exclusively to the dedicated gRPC listener port.
app.MapGrpcService<CampaignSyncService>().RequireLocalPort(grpcPort);

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
      ____                            _              __     __          _ _   
     / ___|__ _ _ __ ___  _ __   __ _(_) __ _ _ __   \ \   / /_ _ _   _| | |_ 
    | |   / _` | '_ ` _ \| '_ \ / _` | |/ _` | '_ \   \ \ / / _` | | | | | __|
    | |__| (_| | | | | | | |_) | (_| | | (_| | | | |   \ V / (_| | |_| | | |_ 
     \____\__,_|_| |_| |_| .__/ \__,_|_|\__, |_| |_|    \_/ \__,_|\__,_|_|\__|
                         |_|            |___/                                 
    ");
    Console.Error.WriteLine($"MCP Version: 0.2.0");
    Console.Error.WriteLine($"Database Path: {dbPathSetting}");
    Console.Error.WriteLine($"Auth Enabled: {authEnabled}");
    Console.Error.WriteLine($"MCP / HTTP:  http://localhost:{mcpPort}");
    Console.Error.WriteLine($"gRPC Sync:   http://localhost:{grpcPort}");
    Console.Error.WriteLine("Listening on:");
    foreach (var address in addresses)
    {
        Console.Error.WriteLine($"  - {address}");
    }
    Console.Error.WriteLine("--------------------------------------------------");
});

app.Run();
