using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Middleware;
using CampaignVault.Models;
using CampaignVault.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Regression coverage for the "silent empty Content" bug: McpResponseCleaner used to replace
/// Content with just the ToolResult's Summary (d988e00 "fix: optimize token usage"), which
/// starved every MCP host that only forwards Content into the model's context — opencode among
/// them. A 2026-08-20 playtest transcript showed get_entity/search_world/recall_history all
/// coming back as one-line summaries, with the model's own reasoning noting it never received
/// any real world data. StructuredContent still had everything; it just never reaches the model
/// on hosts that don't read it.
///
/// These tests run REAL backend-generated ToolResult payloads (via RavenDB-backed tool calls,
/// not hand-written JSON fixtures) through the exact Strip+Sync steps McpResponseCleaner.Register
/// wires into every tool call, and assert Content ends up non-empty and actually carries the
/// generated data. If a future edit reintroduces a summary-only (or empty) Content block, these
/// fail loudly instead of only showing up as a confused model mid-playtest.
/// </summary>
[Collection("RavenDB")]
public class McpResponseCleanerBackendIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public McpResponseCleanerBackendIntegrationTests(RavenDBFixture fixture) => _fixture = fixture;

    private static string NewSlug(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    // Mirrors the camelCase naming policy the MCP SDK actually uses to build StructuredContent
    // (ModelContextProtocol.McpJsonUtilities.DefaultOptions) — see McpResponseCleaner's own
    // VectorFieldsToStrip comment for why the exact wire casing matters here: a PascalCase
    // fixture would silently hide bugs that only reproduce with the real casing.
    private static readonly JsonSerializerOptions WireOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Runs a real ToolResult&lt;T&gt; through the same two steps McpResponseCleaner.Register's
    /// filter applies on every successful call (strip vectors, then sync Content to the cleaned
    /// StructuredContent), via reflection since both are private. Mirrors the reflection approach
    /// already used in McpResponseCleanerTests.cs.
    /// </summary>
    private static string RunThroughCleanerPipeline<T>(ToolResult<T> toolResult)
    {
        var structured = JsonSerializer.SerializeToElement(toolResult, WireOptions);

        var stripMethod = typeof(McpResponseCleaner).GetMethod(
            "StripVectorsFromElement", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(stripMethod);
        var cleaned = (JsonElement)stripMethod!.Invoke(null, [structured])!;

        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "unset — should be replaced by SyncContentToCleanedStructuredContent" }],
            StructuredContent = structured,
        };

        var syncMethod = typeof(McpResponseCleaner).GetMethod(
            "SyncContentToCleanedStructuredContent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(syncMethod);
        syncMethod!.Invoke(null, [result, cleaned]);

        var block = Assert.Single(result.Content);
        return Assert.IsType<TextContentBlock>(block).Text;
    }

    [Fact]
    public async Task GetEntity_RealNpcData_ProducesNonEmptyContentWithActualFields()
    {
        var slug = NewSlug("cleaner-npc");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);
        var deepDive = TestCampaignToolsFactory.CreateDeepDiveTools(_fixture, repo);

        var build = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Locations = [new LocationUpsertRequest { Id = "locations/cleaner-tavern", Name = "The Cracked Tankard", Description = "A tavern.", Type = LocationType.Building }],
            Characters = [new CharacterUpsertRequest { Id = "chars/cleaner-npc", Name = "Kaelen", CurrentLocationId = "locations/cleaner-tavern" }],
        }, slug);
        Assert.True(build.Success, build.Summary);

        // This is the exact call shape from the playtest transcript: campaign-vault_get_entity
        // on a chars/ id, which came back as just "Psychological context for Kaelen retrieved."
        var npc = await deepDive.GetEntity("chars/cleaner-npc", slug);
        Assert.True(npc.Success, npc.Summary);
        Assert.NotNull(npc.Data);

        var content = RunThroughCleanerPipeline(npc);

        Assert.False(string.IsNullOrWhiteSpace(content));
        // Not just the summary sentence — the actual NPC data must be present, since that's the
        // entire reason a model calls get_entity in the first place.
        Assert.Contains("Kaelen", content);
        Assert.Contains("\"data\"", content);
    }

    [Fact]
    public async Task SearchWorld_RealMatches_ProducesNonEmptyContentWithActualMatches()
    {
        var slug = NewSlug("cleaner-search");
        var repo = _fixture.CreateRepository();
        var tools = TestCampaignToolsFactory.Create(_fixture, repository: repo);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var worldBuilder = TestCampaignToolsFactory.CreateWorldBuilderTools(_fixture, repo);

        var build = await worldBuilder.WorldBuild(new WorldBuildBatch
        {
            Characters = [new CharacterUpsertRequest { Id = "chars/cleaner-search-target", Name = "Old Owen Blacksmith" }],
        }, slug);
        Assert.True(build.Success, build.Summary);

        // Mirrors campaign-vault_search_world from the playtest transcript, which came back as
        // just "Found 21 matches." with none of the 21 matches actually visible to the model.
        var search = await tools.SearchWorld("Old Owen Blacksmith", slug);
        Assert.True(search.Success, search.Summary);
        Assert.NotNull(search.Data);

        var content = RunThroughCleanerPipeline(search);

        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("Old Owen Blacksmith", content);
    }

    [Fact]
    public async Task StartSession_RealCampaignState_ProducesNonEmptyContent()
    {
        var slug = NewSlug("cleaner-session");
        var tools = TestCampaignToolsFactory.Create(_fixture);
        await TestCampaignDefaults.EnsureExistsAsync(tools, slug);
        var session = TestCampaignToolsFactory.CreateTool<SessionTools>(_fixture);

        // Mirrors campaign-vault_start_session from the playtest transcript.
        var result = await session.StartSession(slug);
        Assert.True(result.Success, result.Summary);
        Assert.NotNull(result.Data);

        var content = RunThroughCleanerPipeline(result);

        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.Contains("\"data\"", content);
        // A one-line summary would be far shorter than a real WorldState/Campaign snapshot —
        // catches a regression back to summary-only even if the exact field names drift.
        Assert.True(content.Length > 200, $"Content looked summary-sized ({content.Length} chars): {content}");
    }
}
