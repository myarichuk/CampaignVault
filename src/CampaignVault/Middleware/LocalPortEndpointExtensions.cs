using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CampaignVault.Middleware;

/// <summary>
/// Routes endpoints by the Kestrel listener port instead of the Host header.
/// Host-based matching breaks MCP clients (e.g. Grok Web tunnels) that send
/// "localhost" or "127.0.0.1" without the port suffix.
/// </summary>
public static class LocalPortEndpointExtensions
{
    public static TBuilder RequireLocalPort<TBuilder>(this TBuilder builder, int port)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) =>
            {
                return async invocationContext =>
                {
                    if (invocationContext.HttpContext.Connection.LocalPort != port)
                    {
                        invocationContext.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Results.Empty;
                    }

                    return await next(invocationContext);
                };
            });
        });

        return builder;
    }
}