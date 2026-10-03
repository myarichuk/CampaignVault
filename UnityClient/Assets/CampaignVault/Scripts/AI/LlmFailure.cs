using System;
using System.Text;
using System.Text.RegularExpressions;

namespace CampaignVault.UnityClient.AI
{
    public enum LlmFailureKind
    {
        RateLimit,
        Overloaded,
        Auth,
        ModelNotFound,
        BadRequest,
        Network,
        Timeout,
        Malformed,
        Cancelled,
        Setup,
        Other,
    }

    /// <summary>
    /// Why one model call failed, in two voices: <see cref="Friendly"/> for the
    /// player (what happened, what to press) and <see cref="Technical"/> for a
    /// bug report (copy-pastable, never contains the API key).
    /// </summary>
    public sealed class LlmFailure
    {
        public LlmFailureKind Kind;
        public string Friendly = string.Empty;
        public string Technical = string.Empty;

        /// <summary>HTTP status, 0 when no response arrived.</summary>
        public long Status;

        /// <summary>A retry of the same prompt is worth offering (the player may also have fixed Settings meanwhile).</summary>
        public bool Retryable = true;

        /// <summary>Retrying can't help until Settings change (key rejected, model or endpoint unknown).</summary>
        public bool NeedsSettings { get { return Kind == LlmFailureKind.Auth || Kind == LlmFailureKind.ModelNotFound || Kind == LlmFailureKind.Setup; } }

        /// <summary>What the call needs to describe itself; the driver fills it, tests build it by hand.</summary>
        public sealed class Context
        {
            public string Endpoint = string.Empty;
            public string Model = string.Empty;
            public string RequestMeta = string.Empty;
            public int Attempts = 1;
            public string ApiKey = string.Empty;
            public string Body = string.Empty;
            public DateTime? Now;
        }

        public static LlmFailureKind KindFor(long status, string raw)
        {
            string text = (raw ?? string.Empty).ToLowerInvariant();
            if (status == 429) { return LlmFailureKind.RateLimit; }
            if (status == 401 || status == 403) { return LlmFailureKind.Auth; }
            if (status == 404) { return LlmFailureKind.ModelNotFound; }
            if (status == 408) { return LlmFailureKind.Timeout; }
            if (status == 0)
            {
                return text.Contains("timeout") || text.Contains("timed out") ? LlmFailureKind.Timeout : LlmFailureKind.Network;
            }
            if (status == 200)
            {
                return text.Contains("invalid json") ? LlmFailureKind.Malformed : LlmFailureKind.Overloaded;
            }
            if (status == 425 || (status >= 500 && status <= 599)) { return LlmFailureKind.Overloaded; }
            if (status >= 400 && status < 500) { return LlmFailureKind.BadRequest; }
            return LlmFailureKind.Other;
        }

        /// <summary>Builds the failure for a status and the driver's one-line reason.</summary>
        public static LlmFailure From(long status, string raw, Context context)
        {
            context = context ?? new Context();
            var failure = new LlmFailure { Kind = KindFor(status, raw), Status = status };
            failure.Friendly = FriendlyFor(failure.Kind, context.Model);
            failure.Retryable = failure.Kind != LlmFailureKind.Auth
                && failure.Kind != LlmFailureKind.ModelNotFound
                && failure.Kind != LlmFailureKind.Cancelled;
            failure.Technical = TechnicalFor(failure, raw, context);
            return failure;
        }

        /// <summary>A failure that isn't about the transport: an empty reply, a tool that errored. Retrying the prompt is fair.</summary>
        public static LlmFailure Malformed(string what, Context context)
        {
            context = context ?? new Context();
            var failure = new LlmFailure { Kind = LlmFailureKind.Malformed, Status = 200 };
            failure.Friendly = "The AI answered, but not with anything the DM could use (" + what + "). Nothing was lost: press RETRY to ask again, or try another model in Settings.";
            failure.Technical = TechnicalFor(failure, what, context);
            return failure;
        }

        /// <summary>The provider isn't set up (no key, no model, bad URL): nothing was sent. The reason already says what to fix.</summary>
        public static LlmFailure Setup(string reason, Context context)
        {
            context = context ?? new Context();
            var failure = new LlmFailure { Kind = LlmFailureKind.Setup, Retryable = false, Friendly = (reason ?? string.Empty).Trim() + " Open Settings to fix it." };
            failure.Technical = TechnicalFor(failure, reason, context);
            return failure;
        }

        public static LlmFailure Cancelled(Context context)
        {
            context = context ?? new Context();
            var failure = new LlmFailure { Kind = LlmFailureKind.Cancelled, Retryable = false, Friendly = "Stopped." };
            failure.Technical = TechnicalFor(failure, "stopped by the player", context);
            return failure;
        }

        /// <summary>Anything else that went wrong in a turn; the text is shown as is.</summary>
        public static LlmFailure Other(string text, Context context)
        {
            context = context ?? new Context();
            var failure = new LlmFailure { Kind = LlmFailureKind.Other, Friendly = text ?? string.Empty };
            failure.Technical = TechnicalFor(failure, text, context);
            return failure;
        }

        private static string FriendlyFor(LlmFailureKind kind, string model)
        {
            const string again = "Nothing was lost: press RETRY to send it again. If this keeps happening, try another model in Settings.";
            switch (kind)
            {
                case LlmFailureKind.RateLimit:
                    return "The AI service is getting too many requests right now, so the DM couldn't answer. " + again;
                case LlmFailureKind.Overloaded:
                    return "The AI service is overloaded or dropped the connection, so the DM couldn't answer. " + again;
                case LlmFailureKind.Timeout:
                    return "The AI service took too long to answer. " + again;
                case LlmFailureKind.Network:
                    return "Couldn't reach the AI service. Check your internet connection (or that your local server is running), then press RETRY.";
                case LlmFailureKind.Auth:
                    return "The AI provider rejected your API key. Open Settings, check the key, then retry.";
                case LlmFailureKind.ModelNotFound:
                    return "The AI provider doesn't know the model \"" + model + "\" (or this endpoint). Open Settings and pick another model, then retry.";
                case LlmFailureKind.BadRequest:
                    return "The AI provider refused the request. Press RETRY once; if it happens again, try another model in Settings and open Details for the provider's reason.";
                case LlmFailureKind.Malformed:
                    return "The AI service sent back something unreadable. " + again;
                default:
                    return "Something went wrong talking to the AI service. " + again;
            }
        }

        private static string TechnicalFor(LlmFailure failure, string raw, Context context)
        {
            var sb = new StringBuilder();
            DateTime now = context.Now ?? DateTime.UtcNow;
            sb.Append("CampaignVault LLM failure\n");
            sb.Append("time: ").Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")).Append('\n');
            sb.Append("kind: ").Append(failure.Kind).Append('\n');
            sb.Append("http status: ").Append(failure.Status == 0 ? "none (no response)" : failure.Status.ToString()).Append('\n');
            sb.Append("endpoint: ").Append(EndpointLabel(context.Endpoint)).Append('\n');
            sb.Append("model: ").Append(context.Model).Append('\n');
            if (!string.IsNullOrEmpty(context.RequestMeta)) { sb.Append("request: ").Append(context.RequestMeta).Append('\n'); }
            sb.Append("attempts: ").Append(context.Attempts).Append('\n');
            sb.Append("reason: ").Append(raw ?? string.Empty).Append('\n');
            if (!string.IsNullOrEmpty(context.Body)) { sb.Append("provider response:\n").Append(context.Body).Append('\n'); }
            return Redact(sb.ToString().TrimEnd(), context.ApiKey);
        }

        /// <summary>Scheme, host and path only: never the query string, which some endpoints use for keys.</summary>
        internal static string EndpointLabel(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) { return "(unknown)"; }
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri)) { return "(unparsable)"; }
            return uri.Scheme + "://" + uri.Authority.Substring(uri.Authority.LastIndexOf('@') + 1) + uri.AbsolutePath;
        }

        private static readonly Regex Bearer = new Regex(@"(?i)bearer\s+[^\s""',}]+", RegexOptions.Compiled);
        private static readonly Regex KeyLike = new Regex(@"\b(sk|pk|rk|key|gsk|xai)[-_][A-Za-z0-9_\-]{8,}", RegexOptions.Compiled);
        private static readonly Regex Labelled = new Regex(@"(?i)(authorization|api[_-]?key|x-api-key|apikey|access[_-]?token)(""?\s*[:=]\s*""?)[^\s""',}&]+", RegexOptions.Compiled);
        private static readonly Regex QueryKey = new Regex(@"(?i)([?&](?:key|api_key|apikey|token)=)[^&\s""']+", RegexOptions.Compiled);

        /// <summary>Strips the API key and anything shaped like one or like an auth header.</summary>
        public static string Redact(string text, string apiKey)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }
            if (!string.IsNullOrEmpty(apiKey) && apiKey.Length >= 4) { text = text.Replace(apiKey, "[redacted]"); }
            text = Bearer.Replace(text, "Bearer [redacted]");
            text = Labelled.Replace(text, "$1$2[redacted]");
            text = QueryKey.Replace(text, "$1[redacted]");
            text = KeyLike.Replace(text, "[redacted]");
            return text;
        }
    }
}
