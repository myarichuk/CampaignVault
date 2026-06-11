using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using System.Collections.ObjectModel;

namespace CampaignVault.Authoring.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly CampaignAuthoringSettings _settings;

    [ObservableProperty]
    private decimal? _mcpPortValue;

    [ObservableProperty]
    private string _llmProvider = "None";

    [ObservableProperty]
    private string _llmApiKey = string.Empty;

    [ObservableProperty]
    private string _llmEndpoint = string.Empty;

    [ObservableProperty]
    private string _llmModel = string.Empty;

    public ObservableCollection<string> LlmProviders { get; } = new() { "None", "Ollama", "OpenAI", "Gemini" };

    public SettingsViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.LoadSettings();

        _mcpPortValue = _settings.McpPort;
        _llmProvider = _settings.LlmProvider;
        _llmApiKey = _settings.LlmApiKey;
        _llmEndpoint = _settings.LlmEndpoint;
        _llmModel = _settings.LlmModel;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.McpPort = McpPortValue.HasValue ? (int)McpPortValue.Value : 8080;
        _settings.LlmProvider = LlmProvider;
        _settings.LlmApiKey = LlmApiKey;
        _settings.LlmEndpoint = LlmEndpoint;
        _settings.LlmModel = LlmModel;

        _settingsService.SaveSettings(_settings);
    }
}
