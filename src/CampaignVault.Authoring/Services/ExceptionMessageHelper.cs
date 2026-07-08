using System;
using System.IO;
using CampaignVault.Authoring.Vault;
using Grpc.Core;

namespace CampaignVault.Authoring.Services;

public static class ExceptionMessageHelper
{
    public static string ToFriendlyMessage(this Exception ex, string fallbackContext)
    {
        return ex switch
        {
            VaultException vaultEx => vaultEx.Message,

            RpcException rpcEx => rpcEx.StatusCode switch
            {
                StatusCode.Unavailable => "Could not reach the Campaign Vault server. Check your connection settings.",
                StatusCode.DeadlineExceeded => "The server did not respond in time.",
                StatusCode.Unauthenticated or StatusCode.PermissionDenied => "Authentication failed. Check your gRPC token in Settings.",
                _ => $"Server error: {rpcEx.Status.Detail}"
            },

            IOException => "A file could not be accessed. It may be open elsewhere or you may lack permission.",

            UnauthorizedAccessException => "A file could not be accessed. It may be open elsewhere or you may lack permission.",

            System.Text.Json.JsonException => "Data could not be read (corrupt or unexpected format).",

            OperationCanceledException or TimeoutException => "The operation timed out.",

            _ => $"{fallbackContext}: {ex.Message}"
        };
    }
}
