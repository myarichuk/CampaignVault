using System.IO;
using System.Text;
using System.Threading.Tasks;
using CampaignVault.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CampaignVault.Tests;

public class McpResponseEscapingMiddlewareTests
{
    private static readonly int[] McpPorts = [5275, 5443];

    private static DefaultHttpContext CreateContext(int localPort)
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = localPort;
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task InvokeAsync_NonMcpPort_NeverBuffersResponseBody()
    {
        // Regression test for the gRPC-buffering gotcha: this middleware shares Kestrel's pipeline with
        // the gRPC sync service on a different port. If it ever swaps Response.Body for a buffer on a
        // non-MCP port, gRPC streaming loses incremental flushing. Assert next() sees the ORIGINAL
        // Response.Body instance untouched, not a substituted MemoryStream.
        var context = CreateContext(localPort: 50051); // gRPC port, not in McpPorts
        var originalBody = context.Response.Body;

        Stream? observedBody = null;
        RequestDelegate next = ctx =>
        {
            observedBody = ctx.Response.Body;
            return Task.CompletedTask;
        };

        var middleware = new McpResponseEscapingMiddleware(next, McpPorts);
        await middleware.InvokeAsync(context);

        Assert.Same(originalBody, observedBody);
        Assert.Same(originalBody, context.Response.Body);
    }

    [Fact]
    public async Task InvokeAsync_McpPort_JsonQuotesRewrittenWithRelaxedEscaping()
    {
        var context = CreateContext(localPort: 5275);
        context.Response.ContentType = "application/json";

        RequestDelegate next = ctx =>
        {
            var bytes = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":{"text":"say \"hi\""}}""");
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        };

        var responseStream = new MemoryStream();
        context.Response.Body = responseStream;

        var middleware = new McpResponseEscapingMiddleware(next, McpPorts);
        await middleware.InvokeAsync(context);

        responseStream.Position = 0;
        var result = await new StreamReader(responseStream, Encoding.UTF8).ReadToEndAsync();

        // Relaxed encoder emits \" (2 bytes) for an embedded quote, not " (6 bytes).
        Assert.Contains("\\\"hi", result);
        Assert.DoesNotContain("\\u0022", result);
    }
}
