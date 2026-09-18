using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Middleware;

/// <summary>
/// Re-emits the outer MCP JSON-RPC response envelope with relaxed JSON escaping instead of the SDK's
/// built-in HTML-safe default. <see cref="McpResponseCleaner"/> already serializes each tool's inner
/// Content.Text with <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>, but that text is itself
/// embedded as a JSON string value inside the outer envelope, which the SDK serializes with
/// ModelContextProtocol.McpJsonUtilities.DefaultOptions — a frozen, read-only singleton (confirmed: its
/// Encoder is null, i.e. the strict JavaScriptEncoder.Default, and mutating it throws
/// InvalidOperationException once the SDK has touched it, so there's no supported way to configure this
/// from Program.cs). That default encoder escapes a plain ASCII quote as """ (6 bytes) instead of
/// "\"" (2 bytes) — so every quote character already inside our relaxed-escaped Content.Text pays the
/// expensive rate a second time when the envelope wraps it.
///
/// This buffers the final MCP response and re-emits it through the relaxed encoder — a pure
/// wire-format transform, no semantic change. Two shapes are handled: a plain `application/json` body
/// (single JSON-RPC object), and the `text/event-stream` framing the SDK actually uses for every
/// request in this server's stateless HTTP transport (confirmed live: every `tools/call` response comes
/// back as a single `event: message\ndata: {...}\n\n` block, never bare JSON) — each `data:` line's JSON
/// payload is parsed and re-serialized in place, leaving the `event:`/blank-line framing untouched.
/// </summary>
public class McpResponseEscapingMiddleware(RequestDelegate next)
{
    private const string DataPrefix = "data: ";

    private static readonly JsonSerializerOptions RelaxedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;

        var contentType = context.Response.ContentType;
        var isJson = contentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true;
        var isEventStream = contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

        if (context.Response.HasStarted || buffer.Length == 0 || !(isJson || isEventStream))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody);
            return;
        }

        try
        {
            using var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true);
            var bodyText = await reader.ReadToEndAsync();

            var rewritten = isEventStream ? RewriteEventStream(bodyText) : RewriteJson(bodyText);
            if (rewritten != null)
            {
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                context.Response.ContentLength = bytes.Length;
                await originalBody.WriteAsync(bytes);
                return;
            }
        }
        catch
        {
            // Malformed/unparseable body: fall through and write the original bytes unchanged
            // rather than dropping the response.
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody);
    }

    private static string? RewriteJson(string bodyText)
    {
        var node = JsonNode.Parse(bodyText);
        return node?.ToJsonString(RelaxedOptions);
    }

    private static string RewriteEventStream(string bodyText)
    {
        // SSE lines are '\n'-terminated (confirmed live); each event is "event: message\ndata:
        // {json}\n\n". Only the data payload needs re-encoding — event/id lines and blank
        // separators pass through verbatim.
        var lines = bodyText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                var json = lines[i][DataPrefix.Length..];
                var node = JsonNode.Parse(json);
                if (node != null)
                {
                    lines[i] = DataPrefix + node.ToJsonString(RelaxedOptions);
                }
            }
        }

        return string.Join('\n', lines);
    }
}
