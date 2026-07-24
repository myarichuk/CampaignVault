using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace CampaignVault.Middleware;

/// <summary>
/// Per-call telemetry for every MCP tool invocation: tool name, campaign, duration, serialized
/// response size, and error state. This is the objective chattiness measure — response size per
/// tool is what actually costs LLM context, not description length.
/// </summary>
internal static class McpToolTelemetryFilter
{
    /// <summary>Set once after the host is built so the filter can log through the app's providers.</summary>
    internal static ILoggerFactory? LoggerFactory { get; set; }

    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();
            CallToolResult? result = null;
            string? failure = null;
            try
            {
                result = await next(request, cancellationToken);
                return result;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var logger = LoggerFactory?.CreateLogger("CampaignVault.ToolTelemetry");
                if (logger is not null && logger.IsEnabled(LogLevel.Information))
                {
                    var toolName = request.Params?.Name ?? "unknown";
                    var campaign = "-";
                    if (request.Params?.Arguments is { } args &&
                        args.TryGetValue("campaignName", out var campaignEl) &&
                        campaignEl.ValueKind == JsonValueKind.String)
                    {
                        campaign = campaignEl.GetString() ?? "-";
                    }

                    var responseChars = MeasureResponseChars(result);

                    logger.LogInformation(
                        "tool={ToolName} campaign={Campaign} durationMs={DurationMs} responseChars={ResponseChars} isError={IsError} exception={Exception}",
                        toolName,
                        campaign,
                        stopwatch.ElapsedMilliseconds,
                        responseChars,
                        result?.IsError == true,
                        failure ?? "-");
                }
            }
        });
    }

    private static int MeasureResponseChars(CallToolResult? result)
    {
        if (result is null)
        {
            return 0;
        }

        var chars = 0;
        try
        {
            if (result.StructuredContent is { } structured)
            {
                chars += JsonSerializer.Serialize(structured).Length;
            }

            foreach (var block in result.Content ?? [])
            {
                if (block is TextContentBlock text)
                {
                    chars += text.Text?.Length ?? 0;
                }
            }
        }
        catch
        {
            // Telemetry must never fail a tool call over a serialization quirk.
        }

        return chars;
    }
}
