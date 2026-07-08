using System;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.Vault;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;

namespace CampaignVault.Authoring.ViewModels;

public partial class GenerationViewModel : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty] private string _userPrompt = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _generationResult = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isEnabled;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isGenerating;

    [ObservableProperty] private string _selectedEntityType = "character";

    public bool HasResult => !string.IsNullOrEmpty(GenerationResult);

    public bool CanGenerate => IsEnabled && !IsGenerating;

    public GenerationViewModel(SettingsViewModel settingsViewModel)
    {
        _settingsViewModel = settingsViewModel;
        UpdateEnabledState();

        // Subscribe to settings changes
        _settingsViewModel.PropertyChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        _settingsViewModel.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LlmProvider) ||
            e.PropertyName == nameof(SettingsViewModel.LlmApiKey) ||
            e.PropertyName == nameof(SettingsViewModel.LlmEndpoint) ||
            e.PropertyName == nameof(SettingsViewModel.LlmModel))
        {
            UpdateEnabledState();
        }
    }

    private void UpdateEnabledState()
    {
        if (string.IsNullOrEmpty(_settingsViewModel.LlmProvider) || _settingsViewModel.LlmProvider == "None")
        {
            IsEnabled = false;
            StatusMessage = "In-app generation is disabled. Please configure your LLM provider in the Settings tab.";
        }
        else if ((_settingsViewModel.LlmProvider == "OpenAI" || _settingsViewModel.LlmProvider == "Gemini") &&
                 string.IsNullOrWhiteSpace(_settingsViewModel.LlmApiKey))
        {
            IsEnabled = false;
            StatusMessage =
                $"API Key is required for {_settingsViewModel.LlmProvider}. Please configure it in the Settings tab.";
        }
        else
        {
            IsEnabled = true;
            StatusMessage = "Ready. Enter a prompt to generate campaign entities.";
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsGenerating) return;
        if (string.IsNullOrEmpty(UserPrompt)) return;

        IsGenerating = true;
        StatusMessage = "Generating content...";
        GenerationResult = string.Empty;

        try
        {
            var provider = _settingsViewModel.LlmProvider;
            var endpoint = _settingsViewModel.LlmEndpoint;
            var apiKey = _settingsViewModel.LlmApiKey;
            var model = _settingsViewModel.LlmModel;

            IChatClient client;

            if (provider == "Ollama")
            {
                var uri = new Uri(string.IsNullOrEmpty(endpoint) ? "http://localhost:11434/v1" : endpoint);
                client = new OpenAI.Chat.ChatClient(
                    string.IsNullOrEmpty(model) ? "default" : model,
                    new System.ClientModel.ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "ollama" : apiKey),
                    new OpenAI.OpenAIClientOptions { Endpoint = uri }
                ).AsIChatClient();
            }
            else if (provider == "OpenAI")
            {
                var uri = string.IsNullOrEmpty(endpoint) ? null : new Uri(endpoint);
                client = new OpenAI.Chat.ChatClient(
                    string.IsNullOrEmpty(model) ? "default" : model,
                    new System.ClientModel.ApiKeyCredential(apiKey),
                    uri != null ? new OpenAI.OpenAIClientOptions { Endpoint = uri } : null
                ).AsIChatClient();
            }
            else if (provider == "Gemini")
            {
                var uri = string.IsNullOrEmpty(endpoint)
                    ? new Uri("https://generativelanguage.googleapis.com/v1beta/openai/")
                    : new Uri(endpoint);
                client = new OpenAI.Chat.ChatClient(
                    string.IsNullOrEmpty(model) ? "default" : model,
                    new System.ClientModel.ApiKeyCredential(apiKey),
                    new OpenAI.OpenAIClientOptions { Endpoint = uri }
                ).AsIChatClient();
            }
            else
            {
                throw new NotSupportedException($"Provider {provider} is not supported.");
            }

            using (client)
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                var response = await client.GetResponseAsync(new[]
                {
                    new ChatMessage(ChatRole.System,
                        $"You are a TTRPG campaign writer. Generate a campaign entity markdown file with YAML frontmatter. " +
                        $"Generate a {SelectedEntityType} entity. " +
                        $"The output MUST start with '---' and end with the markdown body. Do NOT wrap it in code block ticks (```). Just output the raw content."),
                    new ChatMessage(ChatRole.User, UserPrompt)
                }, new ChatOptions
                {
                    ModelId = string.IsNullOrEmpty(model) ? "default" : model
                }, cts.Token);

                GenerationResult = response.Text ?? string.Empty;
                StatusMessage = "Generation complete!";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "The AI request timed out after 60 seconds.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.ToFriendlyMessage("Generation failed");
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private async Task Insert()
    {
        if (string.IsNullOrEmpty(GenerationResult)) return;

        var mainVm = App.Current?.Services?.GetService(typeof(IWorkspaceState)) as IWorkspaceState;
        if (mainVm == null) return;

        if (mainVm.Workspace.SelectedNode is EntityNodeViewModel)
        {
            mainVm.EditorText = GenerationResult;
            StatusMessage = "Content inserted into editor. Save to persist.";
        }
        else if (mainVm.Session.IsOpen)
        {
            try
            {
                var folder = EntityCreation.GetFolderForType(SelectedEntityType);
                var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var relative = $"{folder}/generated-{ts}.md";

                await mainVm.Session.WriteFileAsync(relative, GenerationResult);
                mainVm.RefreshAll();
                // Try select it
                var found = FindGenerated(mainVm.Workspace.Categories, relative);
                if (found != null) mainVm.Workspace.SelectedNode = found;
                StatusMessage = $"Saved generated content as {relative}.";
            }
            catch (Exception ex)
            {
                mainVm.EditorText = GenerationResult; // fallback to buffer
                StatusMessage = ex.ToFriendlyMessage("Insert to buffer (create failed)");
            }
        }
        else
        {
            mainVm.EditorText = GenerationResult;
            StatusMessage = "Content inserted (open a vault to persist new entities).";
        }
    }

    private static EntityNodeViewModel? FindGenerated(System.Collections.Generic.IEnumerable<ExplorerNodeViewModel> nodes, string rel)
    {
        foreach (var n in nodes)
        {
            if (n is EntityNodeViewModel en && string.Equals(en.Entity.RelativePath, rel, StringComparison.OrdinalIgnoreCase)) return en;
            var deeper = FindGenerated(n.Children, rel);
            if (deeper != null) return deeper;
        }
        return null;
    }
}