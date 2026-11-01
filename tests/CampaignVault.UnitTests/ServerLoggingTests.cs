using System;
using System.IO;
using System.Text.Json;
using CampaignVault.Hosting;
using CampaignVault.Middleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CampaignVault.Tests;

public class ServerLoggingTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "cv_filelog_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveDirectory_DefaultsNextToBinary_AndOffDisables()
    {
        Assert.Equal(Path.Combine("/app", "logs"), FileLoggerProvider.ResolveDirectory(null, "/app"));
        Assert.Equal("/var/cv", FileLoggerProvider.ResolveDirectory(" /var/cv ", "/app"));
        Assert.Null(FileLoggerProvider.ResolveDirectory("OFF", "/app"));
    }

    [Fact]
    public void FileLogger_WritesDailyFile_WithLevelAndCategory()
    {
        var dir = TempDir();
        var now = new DateTime(2026, 11, 1, 9, 30, 0);
        using (var provider = new FileLoggerProvider(dir, () => now))
        {
            provider.CreateLogger("CampaignVault.ToolTelemetry").LogWarning("tool={Tool} failed", "take_turn");
        }

        var text = File.ReadAllText(Path.Combine(dir, "campaignvault-20261101.log"));
        Assert.Contains("[warn] CampaignVault.ToolTelemetry: tool=take_turn failed", text);
    }

    [Fact]
    public void FileLogger_DeletesFilesPastRetention()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "campaignvault-20200101.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-(FileLoggerProvider.RetentionDays + 1)));

        using var provider = new FileLoggerProvider(dir);

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void DescribeFailure_SuccessFalseInText_ReturnsErrorAndSummary()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "{\"success\":false,\"error\":\"InvalidArgument\",\"summary\":\"Unknown field 'activity' at $[0]\"}" }],
        };

        Assert.Equal("InvalidArgument: Unknown field 'activity' at $[0]", McpToolTelemetryFilter.DescribeFailure(result));
    }

    [Fact]
    public void DescribeFailure_SuccessFalseInStructuredContent_ReturnsErrorAndSummary()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { Success = false, Summary = "HpChange skipped" }),
        };

        Assert.Equal("failed: HpChange skipped", McpToolTelemetryFilter.DescribeFailure(result));
    }

    [Fact]
    public void DescribeFailure_IsError_ReturnsMessage()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Invalid arguments" }] };

        Assert.Equal("Invalid arguments", McpToolTelemetryFilter.DescribeFailure(result));
    }

    [Fact]
    public void DescribeFailure_Success_ReturnsNull()
    {
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "{\"success\":true}" }] };

        Assert.Null(McpToolTelemetryFilter.DescribeFailure(result));
    }
}
