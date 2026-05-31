using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CampaignVault.Middleware;

/// <summary>
/// Optional Bearer/X-API-Key Auth Middleware with timing-safe comparison.
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _bearerToken;

    public AuthMiddleware(RequestDelegate next, string bearerToken)
    {
        _next = next;
        _bearerToken = bearerToken;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path == "/" || context.Request.Path == "/health")
        {
            await _next(context);
            return;
        }

        var authorized = false;

        // Preferred: Authorization header (standard and more secure)
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            TimingSafeEquals(authHeader.ToString(), $"Bearer {_bearerToken}"))
        {
            authorized = true;
        }
        // Alternative header (exact match)
        else if (context.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader) &&
                 TimingSafeEquals(apiKeyHeader.ToString(), _bearerToken))
        {
            authorized = true;
        }
        // Query string fallback (for clients like Grok Web custom connectors that cannot set custom headers)
        // SECURITY NOTE: Query parameters are logged in many places. Only use this when header-based auth is not possible.
        else
        {
            var queryToken = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["auth"].ToString();
            if (string.IsNullOrEmpty(queryToken))
                queryToken = context.Request.Query["bearer"].ToString();

            if (TimingSafeEquals(queryToken, _bearerToken))
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

        await _next(context);
    }

    private static bool TimingSafeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
