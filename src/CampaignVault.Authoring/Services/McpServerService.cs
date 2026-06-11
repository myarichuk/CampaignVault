using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using System;
using System.Threading.Tasks;

namespace CampaignVault.Authoring.Services;

public class McpServerService
{
    private IHost? _host;
    private int _currentPort;

    public bool IsRunning => _host != null;

    public async Task StartAsync(int port)
    {
        if (_host != null)
        {
            if (_currentPort == port) return;
            await StopAsync();
        }

        _currentPort = port;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(port);
        });

        // Disable noisy logs
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);

        // Register MCP
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = "CampaignVaultAuthoring",
                Version = "1.0.0"
            };
        })
        .WithHttpTransport()
        .WithToolsFromAssembly();

        // CORS
        builder.Services.AddCors(cors =>
        {
            cors.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });

        var app = builder.Build();
        app.UseCors();
        app.MapMcp("/");

        _host = app;
        await _host.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }
}
