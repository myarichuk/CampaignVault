using System;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>How sure the detector is that a reply is a provider/model refusal.</summary>
    public enum RefusalKind
    {
        None,
        /// <summary>The provider said so: a content_filter/refusal finish reason, a refusal field.</summary>
        Hard,
        /// <summary>A heuristic: a short first-person decline where prose was expected.</summary>
        Soft,
    }

    public struct RefusalVerdict
    {
        public RefusalKind Kind;
        public string Reason;
        public bool IsRefusal { get { return Kind != RefusalKind.None; } }
        public static readonly RefusalVerdict NotRefused = new RefusalVerdict { Kind = RefusalKind.None, Reason = string.Empty };
    }

    /// <summary>
    /// Decides whether a model reply was a refusal (providers differ: Anthropic stop_reason "refusal", OpenAI finish_reason
    /// "content_filter" or message.refusal, OpenRouter a 403 content_policy_violation, xAI undocumented). Pure C#.
    /// The soft heuristic is deliberately narrow, because a false positive throws away a good scene:
    /// it needs a short reply, no tool calls, and a first-person decline or a policy phrase, and callers only ask for it
    /// where the reply is expected to be prose (never for an out-of-character answer).
    /// </summary>
    public static class RefusalDetector
    {
        /// <summary>Real scenes run 350+ words; a decline is a sentence or two.</summary>
        public const int SoftMaxWords = 80;

        private static readonly Regex Opener = new Regex(
            @"^\W*(?:i\s*(?:'m|’m|\s+am)\s+(?:sorry|unable|not\s+able|not\s+going|not\s+comfortable)|i\s+(?:can(?:'|’)?t|cannot|can\s+not|won(?:'|’)?t|will\s+not|must\s+decline|have\s+to\s+decline|apologi[sz]e|(?:don(?:'|’)?t|do\s+not)\s+feel\s+comfortable)|sorry,?\s+(?:but\s+)?i|unfortunately,?\s+i\s+(?:can|am|cannot)|as\s+an\s+ai)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Quoted = new Regex("\"[^\"]*\"|\u201c[^\u201d]*\u201d", RegexOptions.Compiled);

        private static readonly string[] PolicyPhrases =
        {
            "content policy", "usage policy", "my guidelines", "against my guidelines", "violates our", "violate our",
            "can't assist with", "cannot assist with", "can't help with that", "cannot help with that",
            "can't continue with this", "cannot continue with this", "unable to continue with this",
            "not able to continue with this", "i can't write that", "i cannot write that", "i won't write that",
        };

        /// <summary>
        /// Looks at one reply. <paramref name="choice"/> is choices[0] (may be null); <paramref name="allowSoft"/> turns the
        /// prose heuristic on (the caller knows whether a short reply is legitimate here).
        /// </summary>
        public static RefusalVerdict Inspect(JsonValue choice, string content, bool hasToolCalls, bool allowSoft)
        {
            JsonValue message = choice == null || choice.IsNull ? JsonValue.Null : choice.Get("message");
            string finish = choice == null || choice.IsNull ? string.Empty : choice.GetString("finish_reason", string.Empty);
            string stop = choice == null || choice.IsNull ? string.Empty : choice.GetString("stop_reason", string.Empty);
            string native = choice == null || choice.IsNull ? string.Empty : choice.GetString("native_finish_reason", string.Empty);
            string refusal = message.IsNull ? string.Empty : message.GetString("refusal", string.Empty);

            if (!string.IsNullOrWhiteSpace(refusal)) { return Hard("the provider returned a refusal: " + Short(refusal)); }
            if (IsRefusalFinish(finish)) { return Hard("finish_reason=" + finish); }
            if (IsRefusalFinish(stop)) { return Hard("stop_reason=" + stop); }
            if (IsRefusalFinish(native)) { return Hard("native_finish_reason=" + native); }

            if (!allowSoft || hasToolCalls) { return RefusalVerdict.NotRefused; }
            return LooksLikeSoftRefusal(content) ? new RefusalVerdict { Kind = RefusalKind.Soft, Reason = "short decline: " + Short(content) } : RefusalVerdict.NotRefused;
        }

        private static bool IsRefusalFinish(string reason)
        {
            return string.Equals(reason, "content_filter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "refusal", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A short reply that opens with a first-person decline, or names a policy. Scenes are second person and long.</summary>
        public static bool LooksLikeSoftRefusal(string content)
        {
            string text = (content ?? string.Empty).Trim();
            if (text.Length == 0) { return false; }
            if (Storyteller.WordCount(text) > SoftMaxWords) { return false; }
            // A scene addressed to "you" that merely contains a character saying it ("I can't," he says) is not a decline:
            // the opener must be the reply's own first words, not a quoted line.
            string first = text.TrimStart('*', '_', '>', ' ', '\n', '\r');
            if (first.StartsWith("\"", StringComparison.Ordinal) || first.StartsWith("“", StringComparison.Ordinal)) { return false; }
            if (Opener.IsMatch(first)) { return true; }
            // Dialogue is not the model speaking: an NPC may say "can't help with that" inside a perfectly good short scene.
            string lower = Quoted.Replace(text, " ").ToLowerInvariant();
            foreach (string phrase in PolicyPhrases)
            {
                if (lower.Contains(phrase) && (lower.StartsWith("i ", StringComparison.Ordinal) || lower.Contains(" i ") || lower.Contains("i'm") || lower.Contains("i’m"))) { return true; }
            }
            return false;
        }

        /// <summary>
        /// An HTTP-level failure that is a refusal in disguise: OpenRouter's 403 content_policy_violation, a 400 from a moderating
        /// gateway, a 200 whose stream ended in an error. Never a 401 (a bad key), 404, 429 or 5xx, and never "no response" (status 0).
        /// It reads only the driver's one-line reason and the PARSED error object (code, type, message, metadata.error_type): never the
        /// raw body, which can be an HTML block page or a streamed story that happens to say "flagged". A bare "blocked" counts only
        /// beside a content word, so a geo or firewall block is not mistaken for a refusal.
        /// </summary>
        public static bool LooksLikeRefusalError(long status, string raw, string body)
        {
            if (status != 200 && status != 400 && status != 403 && status != 422) { return false; }
            var sb = new System.Text.StringBuilder(raw ?? string.Empty);
            JsonValue parsed;
            if (!string.IsNullOrEmpty(body) && JsonValue.TryParse(body, out parsed) && parsed.Kind == JsonKind.Object)
            {
                JsonValue error = parsed.Get("error");
                if (error.Kind == JsonKind.Object)
                {
                    foreach (string field in new[] { "code", "type", "message" })
                    {
                        JsonValue v = error.Get(field);
                        if (v.Kind == JsonKind.String) { sb.Append(' ').Append(v.StringValue); }
                    }
                    JsonValue kind = error.Get("metadata").Get("error_type");
                    if (kind.Kind == JsonKind.String) { sb.Append(' ').Append(kind.StringValue); }
                }
                else if (error.Kind == JsonKind.String) { sb.Append(' ').Append(error.StringValue); }
            }
            string text = sb.ToString().ToLowerInvariant();
            if (text.Length < 4) { return false; }
            string[] direct =
            {
                "content_policy_violation", "content_filter", "content filter", "content policy", "usage policy", "safety system",
                "safety filter", "moderation", "flagged", "refusal", "refused to", "policy violation", "violates our", "prohibited content",
            };
            foreach (string key in direct) { if (text.Contains(key)) { return true; } }
            if (text.Contains("blocked"))
            {
                string[] beside = { "content", "policy", "safety", "moderation", "prompt", "guideline" };
                foreach (string word in beside) { if (text.Contains(word)) { return true; } }
            }
            return false;
        }

        private static RefusalVerdict Hard(string reason) { return new RefusalVerdict { Kind = RefusalKind.Hard, Reason = reason }; }

        private static string Short(string text)
        {
            string t = (text ?? string.Empty).Replace('\n', ' ').Trim();
            return t.Length <= 120 ? t : t.Substring(0, 120) + "…";
        }
    }
}
