using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CampaignVault.Authoring.Models;
using CampaignVault.Authoring.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampaignVault.Authoring.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly CampaignAuthoringSettings _settings;
    private bool _userDisconnected;

    [ObservableProperty] private decimal? _mcpPortValue;

    [ObservableProperty] private bool _autoStartMcp;

    [ObservableProperty] private string _llmProvider = "None";

    [ObservableProperty] private string _llmApiKey = string.Empty;

    [ObservableProperty] private string _llmEndpoint = string.Empty;

    [ObservableProperty] private string _llmModel = string.Empty;

    [ObservableProperty] private string _grpcHost = "localhost";

    [ObservableProperty] private decimal? _grpcPortValue = 50051;

    [ObservableProperty] private string _grpcToken = string.Empty;

    [ObservableProperty] private decimal? _vaultMcpPortValue = 5275;

    [ObservableProperty] private bool _isMcpRunning;

    [ObservableProperty] private string _mcpStatusText = "Stopped";

    [ObservableProperty] private string _mcpStatusColor = "Red";

    [ObservableProperty] private bool _isGrpcConnected;

    [ObservableProperty] private string _grpcStatusText = "Disconnected";

    [ObservableProperty] private string _grpcStatusColor = "Red";

    public ObservableCollection<string> LlmProviders { get; } = new() { "None", "Ollama", "OpenAI", "Gemini" };

    private Avalonia.Threading.DispatcherTimer? _autoConnectTimer;

    public SettingsViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.LoadSettings();

        _mcpPortValue = _settings.McpPort;
        _autoStartMcp = _settings.AutoStartMcp ?? true;
        _llmProvider = _settings.LlmProvider;
        _llmApiKey = _settings.LlmApiKey;
        _llmEndpoint = _settings.LlmEndpoint;
        _llmModel = _settings.LlmModel;

        _grpcHost = _settings.GrpcHost;
        _grpcPortValue = _settings.GrpcPort;
        _grpcToken = _settings.GrpcToken;
        _vaultMcpPortValue = _settings.VaultMcpPort;

        UpdateMcpStatus();

        _autoConnectTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromSeconds(5)
        };
        _autoConnectTimer.Tick += async (_, _) => await CheckConnectionAsync();
        _autoConnectTimer.Start();

        _ = CheckConnectionAsync();
    }

    private async Task CheckConnectionAsync()
    {
        if (_userDisconnected)
            return;

        var port = ResolveGrpcPort();
        var (success, message) = await VaultGrpcClientFactory.TestConnectionAsync(
            GrpcHost,
            port,
            string.IsNullOrWhiteSpace(GrpcToken) ? null : GrpcToken);

        if (success)
        {
            IsGrpcConnected = true;
            GrpcStatusText = message;
            GrpcStatusColor = "Green";

            var mainVm = WorkspaceService.MainWindowViewModel;
            if (mainVm?.Session.IsOpen == true)
            {
                mainVm.Sync.ConfigureSessionSync();
                mainVm.Sync.UpdateSummary();
                mainVm.UpdateStatusBar();
            }
        }
        else
        {
            SetDisconnected(message);
        }
    }

    private int ResolveGrpcPort() =>
        GrpcPortValue is > 0 and <= 65535 ? (int)GrpcPortValue.Value : 50051;

    private void SetDisconnected(string? statusMessage = null)
    {
        IsGrpcConnected = false;
        GrpcStatusText = statusMessage ?? "Disconnected";
        GrpcStatusColor = "Red";

        var mainVm = WorkspaceService.MainWindowViewModel;
        if (mainVm?.Session.IsOpen == true)
            mainVm.UpdateStatusBar();
    }

    public void UpdateMcpStatus()
    {
        IsMcpRunning = WorkspaceService.McpServerService?.IsRunning ?? false;
        McpStatusText = IsMcpRunning ? $"Running (Port {McpPortValue})" : "Stopped";
        McpStatusColor = IsMcpRunning ? "Green" : "Red";
    }

    [RelayCommand]
    private async Task ToggleMcpServerAsync()
    {
        var service = WorkspaceService.McpServerService;
        if (service == null)
        {
            service = new McpServerService();
            WorkspaceService.McpServerService = service;
        }

        if (service.IsRunning)
            await service.StopAsync();
        else
        {
            var port = McpPortValue.HasValue ? (int)McpPortValue.Value : 8080;
            await service.StartAsync(port);
        }

        UpdateMcpStatus();
    }

    [RelayCommand]
    private async Task ToggleGrpcConnectionAsync()
    {
        if (IsGrpcConnected)
        {
            _userDisconnected = true;
            SetDisconnected("Disconnected (manual)");
            return;
        }

        _userDisconnected = false;
        await CheckConnectionAsync();
    }

    [RelayCommand]
    private void Save()
    {
        _settings.McpPort = McpPortValue.HasValue ? (int)McpPortValue.Value : 8080;
        _settings.AutoStartMcp = AutoStartMcp;
        _settings.LlmProvider = LlmProvider;
        _settings.LlmApiKey = LlmApiKey;
        _settings.LlmEndpoint = LlmEndpoint;
        _settings.LlmModel = LlmModel;

        _settings.GrpcHost = GrpcHost;
        _settings.GrpcPort = ResolveGrpcPort();
        _settings.GrpcToken = GrpcToken;
        _settings.VaultMcpPort = VaultMcpPortValue is > 0 and <= 65535 ? (int)VaultMcpPortValue.Value : 5275;

        _settingsService.SaveSettings(_settings);

        WorkspaceService.MainWindowViewModel?.Sync.ConfigureSessionSync();
    }
}