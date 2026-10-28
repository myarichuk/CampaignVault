#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CampaignVault.UnityClient.Measure
{
    /// <summary>How the model was driven: two calls per turn (a tool loop, then a storyteller), or one call that does both like a web connector.</summary>
    public enum MeasureMode { Storyteller, Connector }

    /// <summary>One scripted line's outcome in one pass over the script.</summary>
    public sealed class BeatResult
    {
        public int Repeat;
        public MeasureBeat Beat;
        public string Narration = string.Empty;
        public List<string> Rolls = new List<string>();
        public int ModelCalls;
        public int ToolCalls;
        public int PromptTokens;
        public int CompletionTokens;
        public int CachedTokens;
        public double Cost;
        public string Error = string.Empty;
        public ProseStats Stats = new ProseStats();
        /// <summary>The scene was checked against what its storyteller was given (storyteller mode, with a scene).</summary>
        public bool Grounded;
        /// <summary>Names in the scene that appear nowhere in the storyteller's brief, earlier scenes or character card.</summary>
        public List<string> Ungrounded = new List<string>();
        /// <summary>The judge read this scene and answered with a list (possibly empty).</summary>
        public bool Judged;
        public List<JudgedInvention> Inventions = new List<JudgedInvention>();
        /// <summary>Why the judge gave no list for this scene (empty when it did, or did not run).</summary>
        public string JudgeError = string.Empty;
        /// <summary>The judge's own call; kept apart from <see cref="Cost"/>, which is what play would cost.</summary>
        public double JudgeCost;
    }

    /// <summary>Everything one run (a mode, over some repeats of the script) produced, plus the provider settings it ran under.</summary>
    public sealed class MeasureRun
    {
        public MeasureMode Mode;
        /// <summary>Whether the remedies of NARRATION_LENGTH_PLAN.md were on for this run (the baseline has them off).</summary>
        public bool Remedies = true;
        public string Provider = string.Empty;
        public string Model = string.Empty;
        public string Settings = string.Empty;
        /// <summary>The storyteller profile when it is not the rules profile (empty: the rules model writes its own scenes).</summary>
        public string Narrator = string.Empty;
        public string NarratorSettings = string.Empty;
        /// <summary>The model that judged faithfulness (empty: the judge was off).</summary>
        public string Judge = string.Empty;
        public DateTime StartedUtc = DateTime.UtcNow;
        public int Repeats = 1;
        /// <summary>Why the run stopped before finishing its passes (empty when it ran to the end or was stopped by the player).</summary>
        public string Failure = string.Empty;
        public readonly List<BeatResult> Beats = new List<BeatResult>();

        public string Title
        {
            get { return (Mode == MeasureMode.Storyteller ? "Storyteller" : "Connector") + (Remedies ? "" : " (baseline)"); }
        }
    }

    /// <summary>Aggregates over a run's narrated scenes.</summary>
    public sealed class MeasureSummary
    {
        public int Scenes;
        public int Failed;
        public double WordsMean;
        public int WordsMin;
        public int WordsMax;
        public double ParagraphsMean;
        public double SentenceMean;
        public double FragmentMean;
        public int InlineRolls;
        public int ShortScenes;
        public double CallsPerTurn;
        public double ToolsPerTurn;
        public double PromptPerTurn;
        public double CompletionPerTurn;
        public double CostTotal;
        /// <summary>Scenes checked for ungrounded names, the names found, and how many of those scenes had any.</summary>
        public int GroundedScenes;
        public int UngroundedNames;
        public int ScenesWithUngrounded;
        /// <summary>Scenes the judge answered for, what it found, the scenes it found something in, and the scenes it failed on.</summary>
        public int JudgedScenes;
        public int Inventions;
        public int ScenesWithInventions;
        public int JudgeFailures;
        public double JudgeCost;
        public readonly SortedDictionary<string, int> InventionsByKind = new SortedDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>A scene under this many words reads as thin for a beat that is not a bare exchange.</summary>
        public const int ShortWords = 200;
        /// <summary>The share of one-to-four-word sentences past which a scene reads as stacked fragments.</summary>
        public const double FragmentFlag = 0.25;
    }

    public static class MeasureReport
    {
        public static MeasureSummary Summarize(MeasureRun run)
        {
            var s = new MeasureSummary { WordsMin = int.MaxValue };
            int turns = 0;
            double words = 0, paras = 0, sent = 0, frag = 0, calls = 0, tools = 0, prompt = 0, completion = 0;
            foreach (BeatResult b in run.Beats)
            {
                turns++;
                calls += b.ModelCalls; tools += b.ToolCalls; prompt += b.PromptTokens; completion += b.CompletionTokens;
                s.CostTotal += b.Cost;
                s.JudgeCost += b.JudgeCost;
                if (b.Narration.Trim().Length == 0 || b.Error.Length > 0) { s.Failed++; continue; }
                s.Scenes++;
                if (b.Grounded)
                {
                    s.GroundedScenes++;
                    s.UngroundedNames += b.Ungrounded.Count;
                    if (b.Ungrounded.Count > 0) { s.ScenesWithUngrounded++; }
                }
                if (b.Judged)
                {
                    s.JudgedScenes++;
                    s.Inventions += b.Inventions.Count;
                    if (b.Inventions.Count > 0) { s.ScenesWithInventions++; }
                    foreach (JudgedInvention inv in b.Inventions)
                    {
                        int n;
                        s.InventionsByKind.TryGetValue(inv.Kind, out n);
                        s.InventionsByKind[inv.Kind] = n + 1;
                    }
                }
                else if (b.JudgeError.Length > 0) { s.JudgeFailures++; }
                words += b.Stats.Words; paras += b.Stats.Paragraphs; sent += b.Stats.MeanSentence; frag += b.Stats.FragmentRatio;
                s.InlineRolls += b.Stats.InlineRolls;
                s.WordsMin = Math.Min(s.WordsMin, b.Stats.Words);
                s.WordsMax = Math.Max(s.WordsMax, b.Stats.Words);
                if (b.Stats.Words < MeasureSummary.ShortWords) { s.ShortScenes++; }
            }
            if (s.Scenes == 0) { s.WordsMin = 0; }
            else
            {
                s.WordsMean = words / s.Scenes; s.ParagraphsMean = paras / s.Scenes;
                s.SentenceMean = sent / s.Scenes; s.FragmentMean = frag / s.Scenes;
            }
            if (turns > 0) { s.CallsPerTurn = calls / turns; s.ToolsPerTurn = tools / turns; s.PromptPerTurn = prompt / turns; s.CompletionPerTurn = completion / turns; }
            return s;
        }

        /// <summary>Markdown for one run: settings, a row per beat (rolls beside the numbers, since the dice differ between runs), then the totals.</summary>
        public static string ToMarkdown(MeasureRun run)
        {
            var sb = new StringBuilder();
            sb.Append("## ").Append(run.Title).Append(" — ").Append(run.Model).Append(run.Narrator.Length > 0 ? ", scenes by " + run.Narrator : string.Empty).Append('\n');
            sb.Append("\n- Provider: ").Append(run.Provider);
            sb.Append("\n- Settings: ").Append(run.Settings);
            if (run.Narrator.Length > 0) { sb.Append("\n- Storyteller: ").Append(run.Narrator).Append(" (").Append(run.NarratorSettings).Append(')'); }
            if (run.Judge.Length > 0) { sb.Append("\n- Faithfulness judge: ").Append(run.Judge); }
            sb.Append("\n- Repeats: ").Append(run.Repeats).Append(", started ").Append(run.StartedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC\n");
            sb.Append("\n| # | beat | words | paras | sent | frag | names | judge | rolls (verdicts) | calls | tools | prompt | out |\n");
            sb.Append("|---|------|------:|------:|-----:|-----:|------:|------:|------------------|------:|------:|-------:|----:|\n");
            int i = 0;
            foreach (BeatResult b in run.Beats)
            {
                i++;
                string rolls = b.Rolls.Count == 0 ? "–" : string.Join("; ", b.Rolls.ToArray());
                string label = b.Error.Length > 0 ? b.Beat.Label + " (ERROR: " + Clip(b.Error, 60) + ")" : b.Beat.Label;
                sb.Append("| ").Append(i).Append(" | ").Append(label.Replace("|", "/")).Append(" | ")
                  .Append(b.Stats.Words).Append(" | ").Append(b.Stats.Paragraphs).Append(" | ")
                  .Append(F1(b.Stats.MeanSentence)).Append(" | ").Append(F2(b.Stats.FragmentRatio)).Append(" | ")
                  .Append(b.Grounded ? b.Ungrounded.Count.ToString(CultureInfo.InvariantCulture) : "–").Append(" | ")
                  .Append(b.Judged ? b.Inventions.Count.ToString(CultureInfo.InvariantCulture) : b.JudgeError.Length > 0 ? "err" : "–").Append(" | ")
                  .Append(Clip(rolls, 90).Replace("|", "/")).Append(" | ")
                  .Append(b.ModelCalls).Append(" | ").Append(b.ToolCalls).Append(" | ")
                  .Append(b.PromptTokens).Append(" | ").Append(b.CompletionTokens).Append(" |\n");
            }
            MeasureSummary s = Summarize(run);
            sb.Append("\n").Append(SummaryLines(s));
            return sb.ToString();
        }

        public static string SummaryLines(MeasureSummary s)
        {
            var sb = new StringBuilder();
            sb.Append("- Scenes: ").Append(s.Scenes).Append(s.Failed > 0 ? " (" + s.Failed + " without narration or failed)" : string.Empty).Append('\n');
            sb.Append("- Words per scene: mean ").Append(F1(s.WordsMean)).Append(", min ").Append(s.WordsMin).Append(", max ").Append(s.WordsMax)
              .Append("; scenes under ").Append(MeasureSummary.ShortWords).Append(" words: ").Append(s.ShortScenes).Append('\n');
            sb.Append("- Paragraphs per scene ").Append(F1(s.ParagraphsMean)).Append(", mean sentence ").Append(F1(s.SentenceMean))
              .Append(" words, fragment share ").Append(F2(s.FragmentMean)).Append(s.FragmentMean > MeasureSummary.FragmentFlag ? " (stacked fragments)" : string.Empty).Append('\n');
            sb.Append("- Roll numbers leaked into prose: ").Append(s.InlineRolls).Append('\n');
            sb.Append("- Per turn: ").Append(F1(s.CallsPerTurn)).Append(" model calls, ").Append(F1(s.ToolsPerTurn)).Append(" tool calls, ")
              .Append(Math.Round(s.PromptPerTurn)).Append(" prompt tokens, ").Append(Math.Round(s.CompletionPerTurn)).Append(" completion tokens\n");
            if (s.CostTotal > 0) { sb.Append("- Cost (as priced): $").Append(s.CostTotal.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n'); }
            if (s.GroundedScenes > 0)
            {
                sb.Append("- Ungrounded names: ").Append(s.UngroundedNames).Append(" in ").Append(s.ScenesWithUngrounded).Append(" of ").Append(s.GroundedScenes)
                  .Append(" scenes (named in the scene, absent from everything the storyteller was given)\n");
            }
            if (s.JudgedScenes > 0 || s.JudgeFailures > 0)
            {
                sb.Append("- Judged inventions: ").Append(s.Inventions).Append(" in ").Append(s.ScenesWithInventions).Append(" of ").Append(s.JudgedScenes).Append(" scenes");
                if (s.InventionsByKind.Count > 0)
                {
                    var kinds = new List<string>();
                    foreach (var pair in s.InventionsByKind) { kinds.Add(pair.Key + " " + pair.Value); }
                    sb.Append(" (").Append(string.Join(", ", kinds.ToArray())).Append(')');
                }
                if (s.JudgeFailures > 0) { sb.Append("; the judge failed on ").Append(s.JudgeFailures); }
                if (s.JudgeCost > 0) { sb.Append("; judging cost $").Append(s.JudgeCost.ToString("0.000", CultureInfo.InvariantCulture)); }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The two modes side by side.</summary>
        public static string Compare(MeasureRun a, MeasureRun b)
        {
            MeasureSummary x = Summarize(a), y = Summarize(b);
            var sb = new StringBuilder();
            sb.Append("## Comparison\n\n| metric | ").Append(a.Title).Append(" | ").Append(b.Title).Append(" |\n|---|---:|---:|\n");
            Row(sb, "scenes", x.Scenes.ToString(CultureInfo.InvariantCulture), y.Scenes.ToString(CultureInfo.InvariantCulture));
            Row(sb, "words per scene (mean)", F1(x.WordsMean), F1(y.WordsMean));
            Row(sb, "words per scene (min)", x.WordsMin.ToString(CultureInfo.InvariantCulture), y.WordsMin.ToString(CultureInfo.InvariantCulture));
            Row(sb, "scenes under " + MeasureSummary.ShortWords + " words", x.ShortScenes.ToString(CultureInfo.InvariantCulture), y.ShortScenes.ToString(CultureInfo.InvariantCulture));
            Row(sb, "paragraphs per scene", F1(x.ParagraphsMean), F1(y.ParagraphsMean));
            Row(sb, "mean sentence length", F1(x.SentenceMean), F1(y.SentenceMean));
            Row(sb, "fragment share", F2(x.FragmentMean), F2(y.FragmentMean));
            Row(sb, "roll numbers in prose", x.InlineRolls.ToString(CultureInfo.InvariantCulture), y.InlineRolls.ToString(CultureInfo.InvariantCulture));
            if (x.GroundedScenes > 0 || y.GroundedScenes > 0)
            {
                Row(sb, "ungrounded names", Measured(x.GroundedScenes, x.UngroundedNames), Measured(y.GroundedScenes, y.UngroundedNames));
                Row(sb, "scenes with ungrounded names", Measured(x.GroundedScenes, x.ScenesWithUngrounded), Measured(y.GroundedScenes, y.ScenesWithUngrounded));
            }
            if (x.JudgedScenes > 0 || y.JudgedScenes > 0)
            {
                Row(sb, "judged inventions", Measured(x.JudgedScenes, x.Inventions), Measured(y.JudgedScenes, y.Inventions));
                Row(sb, "scenes with judged inventions", Measured(x.JudgedScenes, x.ScenesWithInventions), Measured(y.JudgedScenes, y.ScenesWithInventions));
            }
            Row(sb, "model calls per turn", F1(x.CallsPerTurn), F1(y.CallsPerTurn));
            Row(sb, "prompt tokens per turn", Math.Round(x.PromptPerTurn).ToString(CultureInfo.InvariantCulture), Math.Round(y.PromptPerTurn).ToString(CultureInfo.InvariantCulture));
            Row(sb, "completion tokens per turn", Math.Round(x.CompletionPerTurn).ToString(CultureInfo.InvariantCulture), Math.Round(y.CompletionPerTurn).ToString(CultureInfo.InvariantCulture));
            sb.Append("\nDice differ between runs, so compare the means over the repeats, and read the rolls column before blaming a beat.\n");
            return sb.ToString();
        }

        /// <summary>One mode's full export: the report, then every scene verbatim (read them: the numbers don't make the judgement).</summary>
        public static string Export(MeasureRun run)
        {
            var sb = new StringBuilder(ToMarkdown(run));
            sb.Append("\n## Scenes\n");
            int i = 0;
            foreach (BeatResult b in run.Beats)
            {
                i++;
                sb.Append("\n### ").Append(i).Append(". ").Append(b.Beat.Label).Append(" (repeat ").Append(b.Repeat).Append(")\n\n");
                sb.Append("> ").Append(b.Beat.Text).Append("\n\n");
                sb.Append(b.Narration.Trim().Length > 0 ? b.Narration.Trim() : "(no narration)").Append('\n');
                if (b.Grounded)
                {
                    sb.Append("\n**Ungrounded names:** ").Append(b.Ungrounded.Count > 0 ? string.Join(", ", b.Ungrounded.ToArray()) : "none").Append('\n');
                }
                if (b.Judged)
                {
                    sb.Append("\n**Judged inventions:** ").Append(b.Inventions.Count > 0 ? string.Empty : "none").Append('\n');
                    foreach (JudgedInvention inv in b.Inventions)
                    {
                        sb.Append("- ").Append(inv.Kind).Append(": ").Append(inv.Claim);
                        if (inv.Quote.Length > 0 && inv.Quote != inv.Claim) { sb.Append(" (“").Append(inv.Quote).Append("”)"); }
                        sb.Append('\n');
                    }
                }
                else if (b.JudgeError.Length > 0) { sb.Append("\n**Judge failed:** ").Append(b.JudgeError).Append('\n'); }
            }
            return sb.ToString();
        }

        /// <summary>A count, or a dash when the run never measured it (so a missing check never reads as a clean zero).</summary>
        private static string Measured(int measured, int count)
        {
            return measured > 0 ? count.ToString(CultureInfo.InvariantCulture) : "–";
        }

        private static void Row(StringBuilder sb, string metric, string a, string b)
        {
            sb.Append("| ").Append(metric).Append(" | ").Append(a).Append(" | ").Append(b).Append(" |\n");
        }

        private static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string Clip(string text, int max) { return text.Length <= max ? text : text.Substring(0, max - 1) + "…"; }
    }
}
#endif
