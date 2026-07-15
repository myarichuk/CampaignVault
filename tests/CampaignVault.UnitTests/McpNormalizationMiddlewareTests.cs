using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CampaignVault.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class McpNormalizationMiddlewareTests
{
    private sealed class CapturingLogger : ILogger<McpNormalizationMiddleware>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }

    [Fact]
    public async Task InvokeAsync_MalformedBody_LogsWarningAndPassesThroughUnchanged()
    {
        const string malformedBody = "{ this is not valid json";

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(malformedBody));
        context.Request.EnableBuffering();

        string? forwardedBody = null;
        RequestDelegate next = ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            forwardedBody = reader.ReadToEndAsync().GetAwaiter().GetResult();
            return Task.CompletedTask;
        };

        var logger = new CapturingLogger();
        var middleware = new McpNormalizationMiddleware(next, logger);
        await middleware.InvokeAsync(context);

        Assert.Equal(malformedBody, forwardedBody);
        Assert.Contains(LogLevel.Warning, logger.Levels);
        Assert.DoesNotContain(LogLevel.Debug, logger.Levels);
    }

    [Fact]
    public async Task InvokeAsync_RehydratesBody_WhenNoRewritesNeeded()
    {
        const string body = """
                            {
                              "jsonrpc": "2.0",
                              "id": 1,
                              "method": "tools/call",
                              "params": {
                                "name": "commit",
                                "arguments": {
                                  "changes": [{ "$type": "event", "category": "Narrative", "summary": "test" }],
                                  "narrative": "Beat"
                                }
                              }
                            }
                            """;

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.EnableBuffering();

        string? forwardedBody = null;
        RequestDelegate next = ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            forwardedBody = reader.ReadToEndAsync().GetAwaiter().GetResult();
            return Task.CompletedTask;
        };

        var middleware = new McpNormalizationMiddleware(next, NullLogger<McpNormalizationMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        Assert.NotNull(forwardedBody);
        Assert.Contains("\"changes\"", forwardedBody);
        Assert.Contains("\"narrative\"", forwardedBody);
        Assert.False(string.IsNullOrEmpty(forwardedBody));
    }
}
