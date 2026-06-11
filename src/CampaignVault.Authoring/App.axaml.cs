using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CampaignVault.Authoring.Services;
using CampaignVault.Authoring.ViewModels;
using CampaignVault.Authoring.Views;
using System;

namespace CampaignVault.Authoring;

public partial class App : Application
{
    private McpServerService? _mcpServerService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindowViewModel = new MainWindowViewModel();
            WorkspaceService.MainWindowViewModel = mainWindowViewModel;

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            // Start local MCP server
            var settings = mainWindowViewModel.Settings;
            if (settings.McpPortValue.HasValue)
            {
                _mcpServerService = new McpServerService();
                try
                {
                    await _mcpServerService.StartAsync((int)settings.McpPortValue.Value);
                }
                catch (Exception ex)
                {
                    // Fail gracefully
                    Console.Error.WriteLine($"Failed to start MCP server: {ex.Message}");
                }
            }

            // Hook up settings changes to restart server if port changes
            settings.PropertyChanged += async (s, e) =>
            {
                if (e.PropertyName == nameof(settings.McpPortValue) && settings.McpPortValue.HasValue)
                {
                    _mcpServerService ??= new McpServerService();
                    try
                    {
                        await _mcpServerService.StartAsync((int)settings.McpPortValue.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to restart MCP server on port {settings.McpPortValue}: {ex.Message}");
                    }
                }
            };

            desktop.Exit += async (s, e) =>
            {
                if (_mcpServerService != null)
                {
                    await _mcpServerService.StopAsync();
                }
                mainWindowViewModel.Workspace.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}