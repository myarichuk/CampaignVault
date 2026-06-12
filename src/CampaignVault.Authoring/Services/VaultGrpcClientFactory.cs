using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Grpc;
using Grpc.Core;
using Grpc.Net.Client;

namespace CampaignVault.Authoring.Services;

public static class VaultGrpcClientFactory
{
    static VaultGrpcClientFactory()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    public static CampaignSync.CampaignSyncClient CreateClient(string host, int port, string? bearerToken = null)
    {
        var address = $"http://{host}:{port}";

        GrpcChannel channel;
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            // CallCredentials.Create(Insecure, ...) silently drops the credentials on h2c channels
            // because gRPC-dotnet requires TLS for CallCredentials. Inject the header via the
            // HttpClient pipeline instead — this works with both cleartext and TLS.
            var token = bearerToken;
            var httpClient = new HttpClient(new AuthorizationHeaderHandler(token))
            {
                BaseAddress = new Uri(address)
            };
            channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpClient = httpClient });
        }
        else
        {
            channel = GrpcChannel.ForAddress(address);
        }

        return new CampaignSync.CampaignSyncClient(channel);
    }

    /// <summary>
    /// Injects a static Bearer token into every outgoing request at the HttpClient layer.
    /// This is the correct approach for h2c (unencrypted HTTP/2) channels where
    /// <see cref="Grpc.Core.CallCredentials"/> are not honoured.
    /// </summary>
    private sealed class AuthorizationHeaderHandler(string bearerToken) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            return base.SendAsync(request, cancellationToken);
        }
    }


    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        string host,
        int port,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient(host, port, bearerToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            await client.GetCampaignsAsync(
                new EmptyRequest(),
                deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: cts.Token);

            return (true, $"Connected to gRPC sync at {host}:{port}");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated || ex.StatusCode == StatusCode.PermissionDenied)
        {
            return (false, $"Authentication failed ({ex.StatusCode}). Check your sync token matches BEARER_TOKEN on CampaignVault.");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            return (false, $"gRPC sync unavailable at {host}:{port}. Start CampaignVault and confirm it is listening on port {port}.");
        }
        catch (RpcException ex)
        {
            return (false, $"gRPC error ({ex.StatusCode}): {ex.Status.Detail}");
        }
        catch (HttpRequestException ex)
        {
            return (false, DescribeHttpFailure(host, port, ex));
        }
        catch (TaskCanceledException)
        {
            return (false, $"Timed out connecting to gRPC sync at {host}:{port}. Is CampaignVault running?");
        }
        catch (Exception ex) when (ex.InnerException is HttpRequestException httpEx)
        {
            return (false, DescribeHttpFailure(host, port, httpEx));
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private static string DescribeHttpFailure(string host, int port, HttpRequestException ex)
    {
        var detail = ex.InnerException?.Message ?? ex.Message;
        if (detail.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connection refused at {host}:{port}. Start CampaignVault — gRPC sync listens on port {port}, MCP on 5275.";
        }

        return $"Cannot reach gRPC sync at {host}:{port}: {detail}";
    }
}