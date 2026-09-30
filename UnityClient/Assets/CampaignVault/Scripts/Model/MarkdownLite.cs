using System.Text;
using System.Text.RegularExpressions;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// The markdown models actually write (bold, italics, headings, bullets,
    /// quotes) as UI rich text. Everything else stays literal: every '<' in
    /// the source is neutralized first, so model or server text can never
    /// inject size/color/link tags. Pure C#.
    /// </summary>
    public static class MarkdownLite
    {
        // A literal '<' that the rich-text parser shows instead of reading.
        public const string EscapedLessThan = "<noparse><</noparse>";

        private static readonly Regex Bold = new Regex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__");
        private static readonly Regex Italic = new Regex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])|(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])");
        private static readonly Regex Heading = new Regex(@"^#{1,6}\s+(.+?)\s*#*$");
        private static readonly Regex Bullet = new Regex(@"^\s*[-*+]\s+(.*)$");
        private static readonly Regex Numbered = new Regex(@"^\s*(\d{1,3})[.)]\s+(.*)$");
        private static readonly Regex Quote = new Regex(@"^\s*>\s?(.*)$");
        private static readonly Regex Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$");

        public static string ToRichText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) { return string.Empty; }
            string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(markdown.Length + 32);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { sb.Append('\n'); }
                sb.Append(Line(lines[i]));
            }
            return sb.ToString();
        }

        private static string Line(string line)
        {
            if (Rule.IsMatch(line)) { return "<color=#8A7A5099>──────</color>"; }
            Match m = Heading.Match(line);
            if (m.Success) { return "<b><size=115%>" + Inline(m.Groups[1].Value) + "</size></b>"; }
            m = Bullet.Match(line);
            if (m.Success) { return "  • " + Inline(m.Groups[1].Value); }
            m = Numbered.Match(line);
            if (m.Success) { return "  " + m.Groups[1].Value + ". " + Inline(m.Groups[2].Value); }
            m = Quote.Match(line);
            if (m.Success) { return "<i>│ " + Inline(m.Groups[1].Value) + "</i>"; }
            return Inline(line);
        }

        private static string Inline(string text)
        {
            // Escape first: the tags added below are the only tags that survive.
            string escaped = text.Replace("<", EscapedLessThan);
            escaped = Bold.Replace(escaped, delegate (Match m)
            {
                return "<b>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</b>";
            });
            escaped = Italic.Replace(escaped, delegate (Match m)
            {
                return "<i>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</i>";
            });
            return escaped;
        }

        /// <summary>Plain text with every '<' neutralized, for fields that get no markdown (names, tool text).</summary>
        public static string Escape(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.Replace("<", EscapedLessThan);
        }
    }
}
