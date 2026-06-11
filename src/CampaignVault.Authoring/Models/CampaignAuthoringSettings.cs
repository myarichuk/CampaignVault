namespace CampaignVault.Authoring.Models;

public class CampaignAuthoringSettings
{
    public int McpPort { get; set; } = 8080;
    public string LlmProvider { get; set; } = "None"; // Options: "None", "Ollama", "OpenAI", "Gemini"
    public string LlmApiKey { get; set; } = string.Empty;
    public string LlmEndpoint { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;
}
