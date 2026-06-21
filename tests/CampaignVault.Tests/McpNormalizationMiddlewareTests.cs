using CampaignVault.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests;

public class McpNormalizationMiddlewareTests
{
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
