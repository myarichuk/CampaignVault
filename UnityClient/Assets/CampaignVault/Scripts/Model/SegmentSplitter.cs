using System.Collections.Generic;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Model
{
    /// <summary>
    /// Cosmetic splitter for transcript prose. Pure C# (testable outside Unity).
    /// Two hard rules live here:
    /// 1. NPC voice splitting is cosmetic only — the NpcVoice kind selects a
    ///    typeface, never game-mechanical truth.
    /// 2. ExtractRolls must ONLY ever receive tool-result text (callers pass
    ///    McpClient output, never narration), so styled roll chips always carry
    ///    tool provenance and prose can never spoof a roll.
    /// </summary>
    public static class SegmentSplitter
    {
        // Matches `Name: "dialogue"`.
        private static readonly Regex VoicePattern = new Regex(
            @"(?<name>[A-Z][\w' \-]{1,24}):\s*""(?<line>[^""]+)""");

        // Matches `Investigation 10 vs DC 14`. TOOL RESULTS ONLY.
        private static readonly Regex RollPattern = new Regex(
            @"(?<skill>[A-Za-z][A-Za-z ]{1,28}?)\s+(?<roll>\d{1,3})\s+vs\s+DC\s+(?<dc>\d{1,3})",
            RegexOptions.IgnoreCase);

        public static void AddNarration(VaultTranscript transcript, string content)
        {
            string clean = TextSanitizer.Clean(content);
            MatchCollection matches = VoicePattern.Matches(clean);
            if (matches.Count == 0)
            {
                transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = clean });
                return;
            }
            int cursor = 0;
            foreach (Match match in matches)
            {
                if (match.Index > cursor)
                {
                    string before = clean.Substring(cursor, match.Index - cursor).Trim();
                    if (!string.IsNullOrEmpty(before))
                    {
                        transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = before });
                    }
                }
                transcript.Add(new TranscriptSegment
                {
                    Kind = SegmentKind.NpcVoice,
                    Speaker = match.Groups["name"].Value.Trim(),
                    Text = match.Groups["line"].Value.Trim(),
                });
                cursor = match.Index + match.Length;
            }
            if (cursor < clean.Length)
            {
                string after = clean.Substring(cursor).Trim();
                if (!string.IsNullOrEmpty(after))
                {
                    transcript.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = after });
                }
            }
        }

        public static List<TranscriptSegment> ExtractRolls(string toolText)
        {
            var rolls = new List<TranscriptSegment>();
            foreach (Match match in RollPattern.Matches(toolText ?? string.Empty))
            {
                int roll;
                int dc;
                if (!int.TryParse(match.Groups["roll"].Value, out roll)) { continue; }
                if (!int.TryParse(match.Groups["dc"].Value, out dc)) { continue; }
                rolls.Add(new TranscriptSegment
                {
                    Kind = SegmentKind.Roll,
                    Roll = new RollInfo
                    {
                        Label = match.Groups["skill"].Value.Trim(),
                        Detail = roll + " vs DC " + dc,
                        Success = roll >= dc,
                    },
                });
            }
            return rolls;
        }
    }
}
