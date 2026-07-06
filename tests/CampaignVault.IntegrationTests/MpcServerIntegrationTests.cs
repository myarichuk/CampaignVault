using System.Net.Http.Json;
using Testcontainers.Containers;

namespace CampaignVault.IntegrationTests;

/// <summary>
/// Integration tests for the CampaignVault MCP server running in Docker.
/// Verifies DI setup, RavenDB integration, and tool instantiation work correctly.
/// These tests require Docker to be available and the campaignvault:latest image to be built.
/// </summary>
[Collection("Integration Tests")]
public class McpServerIntegrationTests : IAsyncLifetime
{
    private IContainer? _container;
    private HttpClient? _httpClient;
    private const int MCP_PORT = 8080;
    private const string CONTAINER_IMAGE = "campaignvault:latest";

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(CONTAINER_IMAGE)
                .WithPortBinding(MCP_PORT, MCP_PORT)
                .WithEnvironment("CAMPAIGN_DB_PATH", "/app/data/campaign.db")
                .WithEnvironment("MCP_BIND_ANY", "1")
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(
                        r => r
                            .ForPort(MCP_PORT)
                            .ForPath("/health")
                            .ForStatusCode(System.Net.HttpStatusCode.OK),
                        delayBetweenRetries: TimeSpan.FromSeconds(1),
                        maxAttempts: 60))
                .Build();

            await _container.StartAsync();

            var mappedPort = _container.GetMappedPublicPort(MCP_PORT);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{mappedPort}"),
                Timeout = TimeSpan.FromSeconds(30)
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start Docker container. Ensure Docker is running and the campaignvault:latest image is built. " +
                "Build it with: docker build -t campaignvault:latest -f Dockerfile .", ex);
        }
    }

    public async Task DisposeAsync()
    {
        _httpClient?.Dispose();
        if (_container != null)
        {
            try
            {
                await _container.StopAsync();
            }
            finally
            {
                await _container.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task HealthEndpoint_ShouldRespond()
    {
        var response = await _httpClient!.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InfoEndpoint_ShouldReturnServerInfo()
    {
        var response = await _httpClient!.GetAsync("/info");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Campaign Vault", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListCampaigns_ShouldResolveWithoutDiErrors()
    {
        // This test verifies that CampaignRepository and all its dependencies
        // are properly resolved by the Autofac DI container when the tool is instantiated.
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "list_campaigns",
                arguments = new { }
            }
        };

        var response = await _httpClient!.PostAsJsonAsync("/mcp", request);
        var content = await response.Content.ReadAsStringAsync();

        // Verify no DI resolution errors occurred
        Assert.DoesNotContain("Unable to resolve service", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CampaignRepository", content);

        // Should get a valid MCP response
        Assert.True(response.IsSuccessStatusCode, $"Response: {content}");
        Assert.Contains("jsonrpc", content);
    }

    [Fact]
    public async Task GetCurrentCampaign_ShouldResolveWithoutDiErrors()
    {
        // Test another tool to verify DI works for different tool types
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "get_current_campaign",
                arguments = new
                {
                    campaignName = "test-campaign"
                }
            }
        };

        var response = await _httpClient!.PostAsJsonAsync("/mcp", request);
        var content = await response.Content.ReadAsStringAsync();

        // Should not have DI resolution errors (may have domain errors like "campaign not found")
        Assert.DoesNotContain("Unable to resolve service", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CampaignRepository", content);
    }
}
