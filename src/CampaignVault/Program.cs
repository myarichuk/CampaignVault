using Autofac;
using Autofac.Extensions.DependencyInjection;
using CampaignVault.Data;
using CampaignVault.Middleware;
using CampaignVault.Schema;
using CampaignVault.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ModelContextProtocol.Protocol;

// for brevity in the listener

var mcpPort = int.TryParse(Environment.GetEnvironmentVariable("MCP_PORT"), out var configuredMcpPort)
    ? configuredMcpPort
    : 5275;
var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("GRPC_PORT"), out var configuredGrpcPort)
    ? configuredGrpcPort
    : 50051;
var mcpHttpsPort = int.TryParse(Environment.GetEnvironmentVariable("MCP_HTTPS_PORT"), out var configuredMcpHttpsPort)
    ? configuredMcpHttpsPort
    : 5443;
var grpcHttpsPort = int.TryParse(Environment.GetEnvironmentVariable("GRPC_HTTPS_PORT"), out var configuredGrpcHttpsPort)
    ? configuredGrpcHttpsPort
    : 50052;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.PreferHostingUrls(false);

var bindAny = string.Equals(
    Environment.GetEnvironmentVariable("MCP_BIND_ANY"),
    "1",
    StringComparison.OrdinalIgnoreCase) || !builder.Environment.IsDevelopment();

var httpsEnabled = string.Equals(
    Environment.GetEnvironmentVariable("HTTPS_ENABLED"),
    "1",
    StringComparison.OrdinalIgnoreCase) || !builder.Environment.IsDevelopment();

var httpsCertPath = Environment.GetEnvironmentVariable("HTTPS_CERT_PATH");
var httpsCertPassword = Environment.GetEnvironmentVariable("HTTPS_CERT_PASSWORD");

// Most MCP hosts (opencode among them) only forward Content into the model's context and never
// read StructuredContent at all, so populating it by default just doubles every response's token
// cost for no benefit. Off unless a host that actually reads StructuredContent needs it.
var mcpIncludeStructuredContent = string.Equals(
    Environment.GetEnvironmentVariable("MCP_INCLUDE_STRUCTURED_CONTENT"),
    "1",
    StringComparison.OrdinalIgnoreCase);

builder.WebHost.ConfigureKestrel(options =>
{
    // MCP + HTTP health/info
    if (bindAny)
    {
        options.ListenAnyIP(mcpPort, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1AndHttp2; });
        options.ListenAnyIP(grpcPort, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });

        // HTTPS endpoints
        if (httpsEnabled)
        {
            ConfigureHttpsListener(options, mcpHttpsPort, true, HttpProtocols.Http1AndHttp2, httpsCertPath, httpsCertPassword);
            ConfigureHttpsListener(options, grpcHttpsPort, true, HttpProtocols.Http2, httpsCertPath, httpsCertPassword);
        }
    }
    else
    {
        options.ListenLocalhost(mcpPort, listenOptions => { listenOptions.Protocols = HttpProtocols.Http1AndHttp2; });
        options.ListenLocalhost(grpcPort, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });

        // HTTPS endpoints
        if (httpsEnabled)
        {
            ConfigureHttpsListener(options, mcpHttpsPort, false, HttpProtocols.Http1AndHttp2, httpsCertPath, httpsCertPassword);
            ConfigureHttpsListener(options, grpcHttpsPort, false, HttpProtocols.Http2, httpsCertPath, httpsCertPassword);
        }
    }
});

static void ConfigureHttpsListener(
    KestrelServerOptions options,
    int port,
    bool anyIp,
    HttpProtocols protocols,
    string? certPath,
    string? certPassword)
{
    if (anyIp)
    {
        options.ListenAnyIP(port, listenOptions =>
        {
            listenOptions.Protocols = protocols;
            if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
            {
                listenOptions.UseHttps(certPath, certPassword);
            }
            else
            {
                listenOptions.UseHttps();
            }
        });
    }
    else
    {
        options.ListenLocalhost(port, listenOptions =>
        {
            listenOptions.Protocols = protocols;
            if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
            {
                listenOptions.UseHttps(certPath, certPassword);
            }
            else
            {
                listenOptions.UseHttps();
            }
        });
    }
}

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());

var enableStdioTransport = string.Equals(
    Environment.GetEnvironmentVariable("MCP_STDIO"),
    "1",
    StringComparison.OrdinalIgnoreCase);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // stdio transport uses stdout as the JSON-RPC channel — logs there would corrupt the protocol
    // stream, so they must go to stderr instead. HTTP transport (the default) has no such constraint;
    // route logs to stdout there so a normal `dotnet run` console actually shows them — previously
    // this unconditionally forced everything to stderr even in HTTP mode, which is why warnings/errors
    // (e.g. embedding failures) were easy to miss in a terminal only displaying stdout.
    options.LogToStandardErrorThreshold = enableStdioTransport ? LogLevel.Trace : LogLevel.None;
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
builder.Services.AddSingleton<LocalEmbeddingService>();
builder.Services.AddSingleton<ILocalEmbeddingService>(sp => sp.GetRequiredService<LocalEmbeddingService>());

builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
    containerBuilder.RegisterAssemblyModules(typeof(Program).Assembly));

// Install pre-built tool schemas at startup (Phase 2 optimization)
builder.Services.AddCampaignVaultToolSchemas();

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
                .AllowAnyMethod();
        }
        else
        {
            var origins =
                corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    });
});

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
    .WithHttpTransport(options => { options.Stateless = true; })
    .WithToolsFromAssembly()
    .WithRequestFilters(filters =>
    {
        McpToolTelemetryFilter.Register(filters);
        McpToolErrorFilter.Register(filters);
        McpResponseCleaner.Register(filters);
    });

McpResponseCleaner.IncludeStructuredContent = mcpIncludeStructuredContent;

builder.Services.AddGrpc();

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

// Data migrations: handle schema upgrades, format conversions, repairs.
// stdout carries the JSON-RPC channel in MCP_STDIO mode, so startup status goes to stderr.
Console.Error.WriteLine("[Startup] Running data migrations...");
await RavenStartup.RunDataMigrationsAsync(documentStore, loggerFactory);
Console.Error.WriteLine("[Startup] Data migrations complete ✓\n");

// Semantic vector bootstrap: repair any entities missing embeddings
Console.Error.WriteLine("[Startup] Running semantic vector bootstrap...");
var bootstrapLogger = loggerFactory.CreateLogger("SemanticVectorBootstrap");
var bootstrap = new SemanticVectorBootstrap(documentStore, app.Services.GetRequiredService<ILocalEmbeddingService>(), bootstrapLogger);
await bootstrap.RunAsync();
Console.Error.WriteLine("[Startup] Bootstrap complete ✓\n");

McpToolTelemetryFilter.LoggerFactory = loggerFactory;

app.UseCors();

app.UseMiddleware<McpNormalizationMiddleware>();

// Optional Bearer/X-API-Key Auth Middleware
if (!string.IsNullOrEmpty(bearerToken))
{
    app.UseMiddleware<AuthMiddleware>(bearerToken);
}

// Bind MCP + HTTP utility endpoints to both HTTP and HTTPS MCP ports.
// Do not use RequireHost() — Grok Web and other MCP clients often send Host headers
// without a port suffix (e.g. "localhost"), which still 404s with *:port patterns.
var mcpPorts = new[] { mcpPort };
if (httpsEnabled)
{
    mcpPorts = new[] { mcpPort, mcpHttpsPort };
}

app.MapMcp("/").RequireLocalPort(mcpPorts);
app.MapGet("/info", () => "CampaignVault MCP Server (RavenDB) is running.")
    .RequireLocalPort(mcpPorts);
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
    .RequireLocalPort(mcpPorts);

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
    Console.Error.WriteLine($"HTTPS Enabled: {httpsEnabled}");
    Console.Error.WriteLine("MCP HTTP: stateless");
    Console.Error.WriteLine($"MCP Bind: {(bindAny ? "0.0.0.0" : "localhost")}:{mcpPort}");
    Console.Error.WriteLine($"MCP / HTTP:  http://localhost:{mcpPort}");
    if (httpsEnabled)
    {
        Console.Error.WriteLine($"MCP / HTTPS: https://localhost:{mcpHttpsPort}");
    }
    Console.Error.WriteLine($"gRPC Sync:   http://localhost:{grpcPort}");
    if (httpsEnabled)
    {
        Console.Error.WriteLine($"gRPC / HTTPS: https://localhost:{grpcHttpsPort}");
    }
    Console.Error.WriteLine("Listening on:");
    foreach (var address in addresses)
    {
        Console.Error.WriteLine($"  - {address}");
    }

    Console.Error.WriteLine("--------------------------------------------------");
});

app.Run();