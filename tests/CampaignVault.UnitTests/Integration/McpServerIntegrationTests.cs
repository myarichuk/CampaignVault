using System;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests.Integration;

/// <summary>
/// Integration tests for the CampaignVault MCP server.
/// These tests verify DI setup, RavenDB integration, and tool instantiation.
///
/// To run these tests, ensure the MCP server is running on http://localhost:5275
///
/// Via Docker:
///   docker build -t campaignvault:latest .
///   docker run -p 5275:8080 campaignvault:latest
///
/// The Docker image is automatically built during project build via MSBuild target.
/// </summary>
[Trait("Category", "Integration")]
public class McpServerIntegrationTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string MCP_BASE_URL = "http://localhost:5275";

    public McpServerIntegrationTests()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(MCP_BASE_URL),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    [Fact(Skip = "Requires MCP server running on localhost:5275")]
    public async Task HealthEndpoint_ShouldRespond()
    {
        var response = await _httpClient.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact(Skip = "Requires MCP server running on localhost:5275. Tests DI resolution of CampaignRepository.")]
    public async Task ListCampaigns_ShouldResolve_WithoutDiErrors()
    {
        // Call list_campaigns via HTTP
        // This tests that CampaignRepository and all dependencies resolve correctly
        var response = await _httpClient.PostAsync(
            "/mcp",
            new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_campaigns","arguments":{}}}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        var content = await response.Content.ReadAsStringAsync();

        // Should NOT get DI errors
        Assert.DoesNotContain("Unable to resolve service", content, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CampaignRepository", content);

        // Should get a valid response
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.InternalServerError,
            $"Unexpected status: {response.StatusCode}. Response: {content}");

        // If successful, should have a valid MCP response
        if (response.IsSuccessStatusCode)
        {
            Assert.Contains("jsonrpc", content);
        }
    }

    [Fact(Skip = "Requires MCP server running on localhost:5275")]
    public async Task InfoEndpoint_ShouldReturnServerInfo()
    {
        var response = await _httpClient.GetAsync("/info");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Campaign Vault", content, System.StringComparison.OrdinalIgnoreCase);
    }
}
