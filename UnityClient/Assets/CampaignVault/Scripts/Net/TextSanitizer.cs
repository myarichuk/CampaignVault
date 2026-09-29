using System;

namespace CampaignVault.UnityClient.Net
{
    /// <summary>
    /// Hardening helpers shared by every panel. Campaign content is untrusted
    /// input: names and narration can carry control characters, BiDi overrides,
    /// or fake roll lines meant to spoof the game UI. Everything rendered or
    /// logged passes through here first. Pure C# so it can be tested outside Unity.
    /// </summary>
    public static class TextSanitizer
    {
        public const int DefaultMaxLength = 8000;

        public static string Clean(string input, int maxLength)
        {
            if (string.IsNullOrEmpty(input)) { return string.Empty; }
            char[] buffer = new char[input.Length];
            int n = 0;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\n' || c == '\t') { buffer[n++] = c; continue; }
                if (char.IsControl(c)) { continue; }
                if (IsBidiOverride(c)) { continue; }
                if (c == 0x007F) { continue; }
                buffer[n++] = c;
            }
            string cleaned = new string(buffer, 0, n);
            if (maxLength > 0 && cleaned.Length > maxLength)
            {
                cleaned = cleaned.Substring(0, maxLength) + "\u2026 (truncated)";
            }
            return cleaned;
        }

        public static string Clean(string input)
        {
            return Clean(input, DefaultMaxLength);
        }

        private static bool IsBidiOverride(char c)
        {
            // U+202A..U+202E, U+2066..U+2069, U+200E, U+200F, U+061C.
            if (c >= '\u202A' && c <= '\u202E') { return true; }
            if (c >= '\u2066' && c <= '\u2069') { return true; }
            return c == '\u200E' || c == '\u200F' || c == '\u061C';
        }

        /// <summary>
        /// Provider / server URLs must be https, except loopback http for local
        /// play (Ollama, local CampaignVault). Rejects credentials in URLs and
        /// non-http(s) schemes (file://, data:, etc.).
        /// </summary>
        public static bool IsAllowedHttpUrl(string url, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                reason = "URL is empty.";
                return false;
            }
            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
            {
                reason = "URL is not absolute.";
                return false;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                reason = "Only http(s) URLs are allowed.";
                return false;
            }
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                reason = "URLs with embedded credentials are not allowed.";
                return false;
            }
            if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri.Host))
            {
                reason = "Plain http is only allowed for localhost.";
                return false;
            }
            return true;
        }

        private static bool IsLoopback(string host)
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1";
        }

        /// <summary>Scrub secrets out of text before it reaches logs or the screen.</summary>
        public static string Redact(string text, params string[] secrets)
        {
            if (string.IsNullOrEmpty(text)) { return string.Empty; }
            string result = text;
            for (int i = 0; i < secrets.Length; i++)
            {
                string secret = secrets[i];
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
                {
                    result = result.Replace(secret, "***");
                }
            }
            return result;
        }
    }
}
