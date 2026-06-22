using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace CampaignVault.Authoring.Services;

public class McpServerService
{
    private IHost? _host;
    private int _currentPort;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsRunning => _host != null;

    public async Task StartAsync(int port)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_host != null)
            {
                if (_currentPort == port) return;
                await StopInternalAsync();
            }

            _currentPort = port;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(kestrel => { kestrel.ListenAnyIP(port); });

            // Disable noisy logs
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
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
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StopAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task StopInternalAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
            _host = null;
        }
    }
}