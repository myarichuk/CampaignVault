using System;
using System.Collections.Generic;
using System.Globalization;
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
        // `Mirelle: "You're late."` / `**Old Tom:** “Aye.”` at the start of a
        // line or right after a sentence ends. Speaker = 1-4 capitalized words.
        private static readonly Regex VoicePattern = new Regex(
            @"(?:^|(?<=[\n.!?…]\s{0,3}))\*{0,2}(?<name>[A-Z][\w'’\-]*(?: [A-Z][\w'’\-]*){0,3})\*{0,2}:\*{0,2}\s*[""“](?<line>[^""“”]+)[""”]",
            RegexOptions.Multiline);

        // Labels that look like speakers but are the model talking about the text.
        private static readonly HashSet<string> NotSpeakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Note", "Warning", "Tip", "Hint", "Example", "Summary", "Result", "Roll", "OOC", "DM", "GM", "Narrator", "Status",
        };

        // --- Roll narratives, exactly as the server's resolvers word them. ---
        // Actions and targets can't cross a JSON quote or a colon, so a match
        // never swallows the key that precedes the narrative.

        // "Search (Perception): Success. Rolled 17 vs DC 14." (5e + PF2e checks and saves)
        private static readonly Regex CheckPattern = new Regex(
            @"(?<action>[^"":;.\n\\]{1,80}?) \((?<skill>[^()"":\n\\]{1,40})\): (?<verdict>[A-Za-z]+)\. Rolled (?<total>-?\d{1,3}) vs (?:(?<dcname>[A-Za-z]+) )?DC (?<dc>\d{1,3})");

        // "Grapple: CriticalSuccess. Rolled 25 vs Fortitude DC 15." (PF2e grapple/escape)
        private static readonly Regex ManeuverPattern = new Regex(
            @"(?<action>[^"":;.\n\\()]{1,80}?): (?<verdict>CriticalSuccess|Success|Failure|CriticalFailure)\. Rolled (?<total>-?\d{1,3}) vs (?<dcname>[A-Za-z]+) DC (?<dc>\d{1,3})");

        // "Fireball vs Goblin: Failed (Dexterity 9 vs DC 15)" (targeted saves)
        private static readonly Regex TargetSavePattern = new Regex(
            @"(?<action>[^"":;.\n\\]{1,80}?) vs (?<target>[^"":;.\n\\]{1,40}): (?<verdict>[A-Za-z]+) \((?<save>[A-Za-z ]{1,30}) (?<total>-?\d{1,3}) vs DC (?<dc>\d{1,3})\)");

        // "Longsword vs Goblin: Hit for 7 damage. (Attack 17 vs AC 13). CRITICAL HIT!…" (5e)
        // "Longsword vs Goblin: Missed. (Failure) Attack 8 vs AC 13." (PF2e)
        private static readonly Regex AttackPattern = new Regex(
            @"(?<action>[^"":;.\n\\]{1,80}?) vs (?<target>[^"":;.\n\\]{1,40}): (?<hit>Hit for \d+ damage|Missed)[^.""\\]*\.\s*(?:\((?<degree>[A-Za-z]+)\)\s*)?\(?Attack (?<total>-?\d{1,3}) vs AC (?<ac>\d{1,3})\)?");

        // The crit note trails the attack ("… (Attack 25 vs AC 13). CRITICAL HIT!"): read ahead, never consume,
        // so the next roll's text stays free for its own pattern.
        private const int CritLookahead = 60;

        public static void AddNarration(VaultTranscript transcript, string content)
        {
            foreach (var segment in SplitNarration(content)) { transcript.Add(segment); }
        }

        /// <summary>Narration and NPC voice lines, in reading order.</summary>
        public static List<TranscriptSegment> SplitNarration(string content)
        {
            var result = new List<TranscriptSegment>();
            string clean = TextSanitizer.Clean(content);
            int cursor = 0;
            foreach (Match match in VoicePattern.Matches(clean))
            {
                string speaker = match.Groups["name"].Value.Trim();
                if (NotSpeakers.Contains(speaker)) { continue; }
                AddProse(result, clean.Substring(cursor, match.Index - cursor));
                result.Add(new TranscriptSegment
                {
                    Kind = SegmentKind.NpcVoice,
                    Speaker = speaker,
                    Text = match.Groups["line"].Value.Trim(),
                });
                cursor = match.Index + match.Length;
            }
            AddProse(result, clean.Substring(cursor));
            return result;
        }

        private static void AddProse(List<TranscriptSegment> into, string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length > 0) { into.Add(new TranscriptSegment { Kind = SegmentKind.Narration, Text = trimmed }); }
        }

        /// <summary>A found roll and where it sits in the text, so cards follow reading order.</summary>
        private sealed class RollHit
        {
            public int Index;
            public TranscriptSegment Segment;
        }

        /// <summary>
        /// Roll cards from a tool result, carrying the server's verdict. Only
        /// the resolver formats above count: a stray "10 vs DC 14" in prose
        /// fields produces nothing rather than a guessed outcome. The same roll
        /// echoed twice (summary + events) yields one card.
        /// </summary>
        public static List<TranscriptSegment> ExtractRolls(string toolText)
        {
            var hits = new List<RollHit>();
            var result = new List<TranscriptSegment>();
            if (string.IsNullOrEmpty(toolText)) { return result; }
            var seen = new HashSet<string>();
            var claimed = new List<KeyValuePair<int, int>>();

            foreach (Match m in AttackPattern.Matches(toolText))
            {
                bool hit = m.Groups["hit"].Value.StartsWith("Hit", StringComparison.Ordinal);
                RollOutcome outcome;
                if (!TryOutcome(m.Groups["degree"].Value, out outcome))
                {
                    outcome = hit ? RollOutcome.Success : RollOutcome.Failure;
                }
                bool crit = hit && FollowedBy(toolText, m.Index + m.Length, "CRITICAL HIT");
                if (crit) { outcome = RollOutcome.CriticalSuccess; }
                AddRoll(hits, seen, claimed, m,
                    m.Groups["action"].Value.Trim() + " vs " + m.Groups["target"].Value.Trim(),
                    "AC", m.Groups["total"].Value, m.Groups["ac"].Value,
                    crit ? "Critical Hit" : hit ? "Hit" : "Miss", outcome);
            }
            foreach (Match m in TargetSavePattern.Matches(toolText))
            {
                RollOutcome outcome;
                if (!TryOutcome(m.Groups["verdict"].Value, out outcome)) { continue; }
                AddRoll(hits, seen, claimed, m,
                    m.Groups["target"].Value.Trim() + " \u00b7 " + m.Groups["save"].Value.Trim() + " save",
                    "DC", m.Groups["total"].Value, m.Groups["dc"].Value,
                    m.Groups["verdict"].Value, outcome);
            }
            foreach (Match m in CheckPattern.Matches(toolText))
            {
                RollOutcome outcome;
                if (!TryOutcome(m.Groups["verdict"].Value, out outcome)) { continue; }
                string dcName = m.Groups["dcname"].Value;
                AddRoll(hits, seen, claimed, m,
                    m.Groups["skill"].Value.Trim(),
                    dcName.Length > 0 ? dcName + " DC" : "DC", m.Groups["total"].Value, m.Groups["dc"].Value,
                    m.Groups["verdict"].Value, outcome);
            }
            foreach (Match m in ManeuverPattern.Matches(toolText))
            {
                RollOutcome outcome;
                if (!TryOutcome(m.Groups["verdict"].Value, out outcome)) { continue; }
                AddRoll(hits, seen, claimed, m,
                    m.Groups["action"].Value.Trim(),
                    m.Groups["dcname"].Value + " DC", m.Groups["total"].Value, m.Groups["dc"].Value,
                    m.Groups["verdict"].Value, outcome);
            }
            // The patterns run one after another; the cards follow the text.
            hits.Sort(delegate (RollHit a, RollHit b) { return a.Index.CompareTo(b.Index); });
            foreach (var h in hits) { result.Add(h.Segment); }
            return result;
        }

        /// <summary>The marker within a short window after the match, and before the narrative's closing quote.</summary>
        private static bool FollowedBy(string text, int from, string marker)
        {
            int end = Math.Min(text.Length, from + CritLookahead);
            int quote = text.IndexOf('"', from, end - from);
            if (quote >= 0) { end = quote; }
            if (end - from < marker.Length) { return false; }
            return text.IndexOf(marker, from, end - from, StringComparison.Ordinal) >= 0;
        }

        private static void AddRoll(
            List<RollHit> hits, HashSet<string> seen, List<KeyValuePair<int, int>> claimed, Match m,
            string label, string against, string totalText, string targetText, string verdict, RollOutcome outcome)
        {
            // One stretch of text is one roll, whichever pattern saw it first.
            int start = m.Index;
            int end = m.Index + m.Length;
            foreach (var span in claimed)
            {
                if (start < span.Value && span.Key < end) { return; }
            }
            claimed.Add(new KeyValuePair<int, int>(start, end));

            int total;
            int target;
            if (!int.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out total)) { return; }
            if (!int.TryParse(targetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out target)) { return; }
            string detail = total + " vs " + against + " " + target;
            if (!seen.Add(label + "|" + detail + "|" + verdict)) { return; }
            hits.Add(new RollHit
            {
                Index = start,
                Segment = new TranscriptSegment
                {
                    Kind = SegmentKind.Roll,
                    Roll = new RollInfo
                    {
                        Label = label,
                        Detail = detail,
                        Verdict = HumanVerdict(verdict),
                        Outcome = outcome,
                        Total = total,
                        Target = target,
                    },
                },
            });
        }

        /// <summary>Maps every verdict word the resolvers emit; unknown words produce no card.</summary>
        internal static bool TryOutcome(string verdict, out RollOutcome outcome)
        {
            switch (verdict)
            {
                case "CriticalSuccess": outcome = RollOutcome.CriticalSuccess; return true;
                case "Success":
                case "Saved":
                case "Hit": outcome = RollOutcome.Success; return true;
                case "Failure":
                case "Failed":
                case "Missed": outcome = RollOutcome.Failure; return true;
                case "CriticalFailure": outcome = RollOutcome.CriticalFailure; return true;
                default: outcome = RollOutcome.Failure; return false;
            }
        }

        private static string HumanVerdict(string verdict)
        {
            switch (verdict)
            {
                case "CriticalSuccess": return "Critical Success";
                case "CriticalFailure": return "Critical Failure";
                default: return verdict;
            }
        }
    }
}
