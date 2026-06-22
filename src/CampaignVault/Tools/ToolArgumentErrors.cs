using System.Text.Json;
using CampaignVault.Models;

namespace CampaignVault.Tools;

internal static class ToolArgumentErrors
{
    public static Task<ToolResult<T>> Missing<T>(
        string paramName,
        string guidance,
        string? toolName = null,
        string? exampleCall = null)
    {
        string summary;
        JsonElement? retryExample = null;

        if (toolName is not null && ToolCallExamples.TryGet(toolName, out _))
        {
            (summary, retryExample) = ToolCallExamples.BuildMissingParamResponse(toolName, paramName, guidance);
        }
        else
        {
            summary = exampleCall is null
                ? $"Missing required parameter '{paramName}'. {guidance}"
                : $"Missing required parameter '{paramName}'. {guidance} Example: {exampleCall}";
        }

        return Task.FromResult(new ToolResult<T>(
            false,
            Error: ToolErrors.InvalidArgument,
            Summary: summary,
            RetryExample: retryExample));
    }
}