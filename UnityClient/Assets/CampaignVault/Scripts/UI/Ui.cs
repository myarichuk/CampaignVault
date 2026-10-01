using System;
using System.Collections.Generic;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Small text helpers the view models share (names, monograms, long-prose chunks). The element builders that
    /// used to live here are gone: layout is UXML (Templates/), looks are USS (Theme/).
    /// </summary>
    public static class Ui
    {
        /// <summary>Paragraph chunks past a label's display cap: one label each, so long prose shows whole.</summary>
        public const int BlockChunkChars = 6000;

        internal static List<string> Chunks(string text, int max)
        {
            var chunks = new List<string>();
            if (text.Length <= max) { chunks.Add(text); return chunks; }
            int start = 0;
            while (text.Length - start > max)
            {
                int cut = text.LastIndexOf("\n\n", start + max - 1, max - 1, StringComparison.Ordinal);
                if (cut <= start + max / 4) { cut = text.LastIndexOfAny(new[] { ' ', '\n' }, start + max - 1, max - 1); }
                if (cut <= start + max / 4) { cut = start + max; }
                chunks.Add(text.Substring(start, cut - start).TrimEnd());
                start = cut;
                while (start < text.Length && char.IsWhiteSpace(text[start])) { start++; }
            }
            if (start < text.Length) { chunks.Add(text.Substring(start)); }
            return chunks;
        }

        /// <summary>"locations/old-mill" → "Old Mill".</summary>
        public static string PrettyId(string id)
        {
            if (string.IsNullOrEmpty(id)) { return string.Empty; }
            string tail = id.Substring(id.LastIndexOf('/') + 1).Replace('-', ' ').Replace('_', ' ');
            var chars = tail.ToCharArray();
            bool start = true;
            for (int i = 0; i < chars.Length; i++)
            {
                if (start && char.IsLetter(chars[i])) { chars[i] = char.ToUpperInvariant(chars[i]); }
                start = chars[i] == ' ';
            }
            return new string(chars);
        }

        /// <summary>Two-letter monogram for a portrait tile.</summary>
        /// <remarks>Letters and digits only, so "\"Red\" Jack", "  ", or an emoji name never make an empty or broken tile.</remarks>
        public static string Monogram(string name)
        {
            var initials = new System.Text.StringBuilder();
            var firstWord = new System.Text.StringBuilder();
            bool wordStart = true;
            int words = 0;
            foreach (char ch in name ?? string.Empty)
            {
                if (char.IsWhiteSpace(ch)) { wordStart = true; continue; }
                if (!char.IsLetterOrDigit(ch)) { continue; }
                if (wordStart) { words++; if (initials.Length < 2) { initials.Append(ch); } wordStart = false; }
                if (words == 1 && firstWord.Length < 2) { firstWord.Append(ch); }
            }
            string mono = initials.Length >= 2 ? initials.ToString() : firstWord.ToString();
            return mono.Length == 0 ? "?" : mono.ToUpperInvariant();
        }
    }
}
