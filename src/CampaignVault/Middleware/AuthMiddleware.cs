using System.Security.Cryptography;
using System.Text;

namespace CampaignVault.Middleware;

/// <summary>
/// Optional Bearer/X-API-Key Auth Middleware with timing-safe comparison.
/// </summary>
public class AuthMiddleware(RequestDelegate next, string bearerToken, bool allowQueryToken = false)
{
    private readonly byte[] _bearerTokenBytes = Encoding.UTF8.GetBytes(bearerToken);
    private readonly bool _allowQueryToken = allowQueryToken;

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
        // Opt-in query string fallback for header-less clients (see AUTH_ALLOW_QUERY_TOKEN in Program.cs).
        // Off by default: query parameters land in logs, history, and referers.
        else if (_allowQueryToken && TimingSafeEquals(context.Request.Query["token"].ToString(), bearerToken))
        {
            authorized = true;
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