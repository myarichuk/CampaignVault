using CampaignVault.Data;
using CampaignVault.Tools;
using CampaignVault.Middleware;
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
var documentStore = RavenStartup.Initialize(dbPath);

// Services
builder.Services.AddSingleton<IDocumentStore>(documentStore);

// Simulation engine + default rules (extensible via additional AddSingleton<ISimulationRule, ...>)
builder.Services.AddSingleton<ISimulationRule, NeedsAccumulationRule>();
builder.Services.AddSingleton<ISimulationRule, RumorDecayRule>();
builder.Services.AddSingleton<ISimulationRule, ScheduleEvaluationRule>();
builder.Services.AddSingleton<ISimulationRule, StatusExpiryRule>();
builder.Services.AddSingleton<ISimulationRule, TransientEvictionRule>();
builder.Services.AddSingleton<ISimulationRule, FactionEcosystemRule>();
builder.Services.AddSingleton<ISimulationRule, QuestStalenessRule>();
builder.Services.AddSingleton<ISimulationRule, NeedConflictRule>();
builder.Services.AddSingleton<ISimulationRule, MemorySalienceDecayRule>();
builder.Services.AddSingleton<ISimulationRule, RelationalRearmRule>();
builder.Services.AddSingleton<IWorldSimulationEngine, DefaultSimulationEngine>();

// WorldChange handlers (new extensible dispatch system - "ShouldHandle" responsibility pattern)
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.HpChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.ItemTransferHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.StatusChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.EventOccurredHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.RumorEvolvesHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.RelationshipChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.SpatialRelationChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.NeedChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.AttributeChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.MoodChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.ActivityChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.LocationCreateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.LocationUpdateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.CharacterCreateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.ScheduleChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.ItemCreateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.TravelChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.FactionCreateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.FactionReputationChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.FactionStateChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.QuestCreateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.QuestProgressHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.RestChangeHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.ItemUpdateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.CharacterUpdateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.KnowledgeUpdateHandler>();
builder.Services.AddSingleton<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler, CampaignVault.Data.ChangeHandlers.RulesetActionHandler>(sp =>
    new CampaignVault.Data.ChangeHandlers.RulesetActionHandler(
        sp.GetRequiredService<CampaignVault.Rulesets.IRulesetModuleSelector>(),
        sp.GetRequiredService<CampaignDocumentKeys>(),
        sp.GetRequiredService<ICurrentCampaignContext>()));

// Combat / Rulesets
builder.Services.AddSingleton<IRollService, DefaultRollService>();
builder.Services.AddSingleton<CampaignVault.Rulesets.IRulesetModule, CampaignVault.Rulesets.Dnd5eRulesetResolver>();
builder.Services.AddSingleton<CampaignVault.Rulesets.IRulesetModule, CampaignVault.Rulesets.Pf2eRulesetResolver>();
builder.Services.AddSingleton<CampaignVault.Rulesets.IRulesetModule, CampaignVault.Rulesets.Fallout2d20RulesetResolver>();
builder.Services.AddSingleton<CampaignVault.Rulesets.IRulesetModuleSelector, CampaignVault.Rulesets.RulesetModuleSelector>();

// Pressure system (Phase 9 extensibility)
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.AgingRumorPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.UnresolvedEventPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.CharacterDistressPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.DanglingItemPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.NeverVisitedTransientPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.QuestDeadlinePressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.StuckTravelPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.PressureHintEnricher>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.LocationHallucinationPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.LocationIntegrityPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.LocationConnectivityPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.LocationFlavorPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.SceneQuestStalenessPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.TransientQuestGiverPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.MemoryDecayPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.UrgentInitiativePressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.FactionTerritoryPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.FactionOpportunisticPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.FactionEconomyPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureContributor, CampaignVault.Data.Pressure.Contributors.FactionRecentEventPressureContributor>();
builder.Services.AddSingleton<CampaignVault.Data.Pressure.IPressureOrchestrator>(sp =>
    new CampaignVault.Data.Pressure.PressureOrchestrator(
        sp.GetServices<CampaignVault.Data.Pressure.IPressureContributor>(),
        sp.GetRequiredService<IPressureManager>(),
        sp.GetRequiredService<CampaignVault.Rulesets.IRulesetModuleSelector>()));
// Behavioral synthesis (deterministic first, cheap & predictable)
builder.Services.AddSingleton<INpcBehaviorSynthesizer, DefaultBehaviorSynthesizer>();

// NPC initiative (Phase 10 — read-side)
builder.Services.AddSingleton<CampaignVault.Data.Initiative.INpcInitiativeSignalProvider, CampaignVault.Data.Initiative.RelationalInitiativeProvider>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.INpcInitiativeSignalProvider, CampaignVault.Data.Initiative.MemoryInitiativeProvider>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.INpcInitiativeSignalProvider, CampaignVault.Data.Initiative.NeedActivityConflictProvider>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.INpcInitiativeSignalProvider, CampaignVault.Data.Initiative.DispositionInitiativeProvider>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.IRelevantMemorySelector, CampaignVault.Data.Initiative.DefaultRelevantMemorySelector>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.IBehavioralTensionCalculator, CampaignVault.Data.Initiative.DefaultBehavioralTensionCalculator>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.IInitiativeSuppressionStore, CampaignVault.Data.Initiative.CampaignInitiativeSuppressionStore>();
builder.Services.AddSingleton<CampaignVault.Data.Initiative.INpcInitiativeService>(sp =>
    new CampaignVault.Data.Initiative.NpcInitiativeService(
        sp.GetServices<CampaignVault.Data.Initiative.INpcInitiativeSignalProvider>(),
        sp.GetRequiredService<CampaignVault.Data.Initiative.IRelevantMemorySelector>(),
        sp.GetRequiredService<CampaignVault.Data.Initiative.IBehavioralTensionCalculator>(),
        sp.GetRequiredService<CampaignVault.Data.Initiative.IInitiativeSuppressionStore>()));

// Campaign scoping & multi-campaign keying (first-class Campaign model)
builder.Services.AddSingleton<CampaignDocumentKeys>();
builder.Services.AddSingleton<ICurrentCampaignContext, CurrentCampaignContext>();

builder.Services.AddSingleton<CampaignRepository>(sp =>
    new CampaignRepository(
        sp.GetRequiredService<IDocumentStore>(),
        sp.GetRequiredService<IWorldSimulationEngine>(),
        sp.GetRequiredService<ILogger<CampaignRepository>>(),
        sp.GetRequiredService<INpcBehaviorSynthesizer>(),
        sp.GetRequiredService<CampaignDocumentKeys>(),
        sp.GetRequiredService<ICurrentCampaignContext>(),
        sp.GetServices<CampaignVault.Data.ChangeHandlers.IWorldChangeHandler>(),
        sp.GetRequiredService<CampaignVault.Data.Initiative.INpcInitiativeService>()));
builder.Services.AddSingleton<IPressureManager, PressureManager>();

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

app.UseMiddleware<McpNormalizationMiddleware>();

// Optional Bearer/X-API-Key Auth Middleware
if (!string.IsNullOrEmpty(bearerToken))
{
    app.UseMiddleware<AuthMiddleware>(bearerToken);
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
