using System.Security.Cryptography;
using System.Text;

namespace CampaignVault.Middleware;

/// <summary>
/// Optional Bearer/X-API-Key Auth Middleware with timing-safe comparison.
/// </summary>
public class AuthMiddleware(RequestDelegate next, string bearerToken)
{
    private readonly byte[] _bearerTokenBytes = Encoding.UTF8.GetBytes(bearerToken);

    public async Task InvokeAsync(HttpContext context)
    {
        // /health is a public liveness probe and must always be reachable by orchestrators.
        // Every other path (MCP at "/", gRPC services, /info) requires authentication.
        if (context.Request.Path == "/health")
        {
            await next(context);
            return;
        }

        var authorized = false;

        // Preferred: Authorization header (standard and more secure)
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            TimingSafeEquals(authHeader.ToString(), $"Bearer {bearerToken}"))
        {
            authorized = true;
        }
        // Alternative header (exact match)
        else if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader) &&
                 TimingSafeEquals(apiKeyHeader.ToString(), bearerToken))
        {
            authorized = true;
        }
        // Query string fallback (for clients like Grok Web custom connectors that cannot set custom headers)
        // SECURITY NOTE: Query parameters are logged in many places. Only use this when header-based auth is not possible.
        else
        {
            var queryToken = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(queryToken))
            {
                queryToken = context.Request.Query["auth"].ToString();
            }

            if (string.IsNullOrEmpty(queryToken))
            {
                queryToken = context.Request.Query["bearer"].ToString();
            }

            if (TimingSafeEquals(queryToken, bearerToken))
            {
                authorized = true;
            }
        }

        if (!authorized)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await next(context);
    }

    private bool TimingSafeEquals(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = ReferenceEquals(b, bearerToken) ? _bearerTokenBytes : Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}