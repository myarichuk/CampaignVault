namespace CampaignVault.Authoring.Models;

public class CampaignAuthoringSettings
{
    public int McpPort { get; set; } = 8080;
    public bool? AutoStartMcp { get; set; } = true;
    public string LlmProvider { get; set; } = "None"; // Options: "None", "Ollama", "OpenAI", "Gemini"
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmEndpoint { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;

    // CampaignVault gRPC sync (authoring tool → RavenDB), separate from the MCP play server
    public string GrpcHost { get; set; } = "localhost";
    public int GrpcPort { get; set; } = 50051;
    public string GrpcToken { get; set; } = string.Empty;

    // Reference only — CampaignVault MCP for live play sessions (not used for sync)
    public int VaultMcpPort { get; set; } = 5275;
}
