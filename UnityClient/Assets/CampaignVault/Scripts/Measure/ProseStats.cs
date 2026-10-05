#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CampaignVault.UnityClient.Measure
{
    /// <summary>
    /// Prose statistics for one scene (NARRATION_LENGTH_PLAN.md, L1): a C# port of scripts/measure/prose_stats.py so the
    /// client can judge a run without leaving the app. The numbers support a judgement; reading the scene decides it.
    /// The leading roll block that connectors show above a scene is left out first, so it can't pass for prose.
    /// </summary>
    public sealed class ProseStats
    {
        public const int FragmentWords = 5;

        private static readonly Regex WordPattern = new Regex(@"[A-Za-z0-9’'\-]+", RegexOptions.Compiled);
        private static readonly Regex SentenceSplit = new Regex(@"(?<=[.!?…])[""”’)\]*_]*\s+", RegexOptions.Compiled);
        private static readonly Regex ParagraphSplit = new Regex(@"\n\s*\n", RegexOptions.Compiled);
        private static readonly Regex RollMention = new Regex(
            @"\b\d+\s+vs\.?\s+(?:DC|AC)\s*\d+\b|\bRolled\s+\d+|\bd20\s*\(|\bnat(?:ural)?\s*(?:1|20)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // *Investigation 12 vs DC 10: success*  (italic roll lines), a ROLL SUMMARY caption, or a divider.
        private static readonly Regex RollLine = new Regex(
            @"^\s*[*_]+[^\n]*\b(?:vs\.?\s*(?:DC|AC)|DC\s*\d+)\b[^\n]*$|^\s*(?:\**ROLL SUMMARY\**:?|-{3,}|\*{3,}|_{3,})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        public int Words;
        public int Paragraphs;
        public int Sentences;
        public double MeanSentence;
        /// <summary>Share of sentences under <see cref="FragmentWords"/> words ("Kit on." "Dome already dead.").</summary>
        public double FragmentRatio;
        /// <summary>Roll numbers that leaked into the prose ("17 vs DC 14").</summary>
        public int InlineRolls;

        public static string StripRollBlock(string text)
        {
            return RollLine.Replace(text ?? string.Empty, string.Empty).Trim();
        }

        public static ProseStats Measure(string text)
        {
            string prose = StripRollBlock(text);
            var stats = new ProseStats();
            if (prose.Length == 0) { return stats; }
            var lengths = new List<int>();
            int paragraphs = 0;
            foreach (string para in ParagraphSplit.Split(prose))
            {
                if (para.Trim().Length == 0) { continue; }
                paragraphs++;
                string flat = Regex.Replace(para, @"\s+", " ").Trim();
                foreach (string sentence in SentenceSplit.Split(flat))
                {
                    if (WordPattern.IsMatch(sentence)) { lengths.Add(WordPattern.Matches(sentence).Count); }
                }
            }
            stats.Words = WordPattern.Matches(prose).Count;
            stats.Paragraphs = paragraphs;
            stats.Sentences = lengths.Count;
            if (lengths.Count > 0)
            {
                int total = 0, fragments = 0;
                foreach (int n in lengths) { total += n; if (n < FragmentWords) { fragments++; } }
                stats.MeanSentence = System.Math.Round((double)total / lengths.Count, 1);
                stats.FragmentRatio = System.Math.Round((double)fragments / lengths.Count, 2);
            }
            stats.InlineRolls = RollMention.Matches(prose).Count;
            return stats;
        }
    }
}
#endif
