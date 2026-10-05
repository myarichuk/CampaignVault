using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Measure;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The debug narration measurer's pure parts (NARRATION_LENGTH_PLAN.md, L1): the prose statistics, the script and the report.</summary>
    public class MeasureTests
    {
        // The expected numbers come from scripts/measure/prose_stats.py on the same text.
        private const string Sample =
            "Kit on. Dome already dead.\n\n"
            + "You kneel by the ditch while the cold water soaks through both knees, and a wren scolds you from the hedge. Nothing here but mud and a rusted nail!\n\n"
            + "The road waits.";

        [Test]
        public void ProseStats_MatchesThePythonScript()
        {
            var s = ProseStats.Measure(Sample);
            Assert.AreEqual(37, s.Words);
            Assert.AreEqual(3, s.Paragraphs);
            Assert.AreEqual(5, s.Sentences);
            Assert.AreEqual(7.4, s.MeanSentence, 0.001);
            Assert.AreEqual(0.6, s.FragmentRatio, 0.001);
            Assert.AreEqual(0, s.InlineRolls);
        }

        [Test]
        public void ProseStats_CountsRollNumbersThatLeakIntoProse()
        {
            var s = ProseStats.Measure(Sample + "\n\nShe rolled 17 vs DC 14 and wept.");
            Assert.AreEqual(45, s.Words);
            Assert.AreEqual(4, s.Paragraphs);
            Assert.AreEqual(1, s.InlineRolls);
        }

        [Test]
        public void ProseStats_LeavesTheConnectorRollBlockOut()
        {
            string withBlock = "*Investigation 12 vs DC 10: success*\n*Perception 8 vs DC 12: failure*\n\n---\n\n" + Sample;
            var s = ProseStats.Measure(withBlock);
            var plain = ProseStats.Measure(Sample);
            Assert.AreEqual(plain.Words, s.Words, "the block is not prose");
            Assert.AreEqual(plain.Sentences, s.Sentences);
            Assert.AreEqual(plain.FragmentRatio, s.FragmentRatio, 0.001);
            Assert.AreEqual(0, s.InlineRolls, "the block's numbers are not a leak");
        }

        [Test]
        public void ProseStats_EmptyTextIsAllZero()
        {
            var s = ProseStats.Measure("   ");
            Assert.AreEqual(0, s.Words);
            Assert.AreEqual(0, s.Sentences);
            Assert.AreEqual(0.0, s.FragmentRatio, 0.0001);
        }

        // The editor always defines UNITY_EDITOR, so a test here can't compile a release build; it can check the source instead:
        // every measurer type is named only inside a debug-only block (or a file that is one block from top to bottom).
        [Test]
        public void ReleaseBuilds_ContainNoMeasurerCode()
        {
            string scripts = Path.Combine(Application.dataPath, "CampaignVault", "Scripts");
            var tokens = new[] { "MeasurePage", "MeasureRig", "MeasureScript", "MeasureReport", "MeasureRun", "MeasureBeat", "MeasureMode", "ProseStats", "MeasureTab" };
            var gated = new Regex(@"#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD.*?(?:#else|#endif)", RegexOptions.Singleline);
            var problems = new List<string>();
            int wholeFile = 0;
            foreach (string file in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.TrimStart().StartsWith("#if UNITY_EDITOR || DEVELOPMENT_BUILD") && text.TrimEnd().EndsWith("#endif")) { wholeFile++; continue; }
                string rest = gated.Replace(text, string.Empty);
                foreach (string token in tokens)
                {
                    if (rest.Contains(token)) { problems.Add(Path.GetFileName(file) + " names " + token + " outside a debug-only block"); }
                }
            }
            Assert.IsEmpty(problems, string.Join("\n", problems.ToArray()));
            Assert.GreaterOrEqual(wholeFile, 5, "the measurer files are each one debug-only block");
        }

        [Test]
        public void Script_HasTheEightBeats_AndASeedThatParses()
        {
            Assert.AreEqual(8, MeasureScript.Beats.Count);
            var ids = new HashSet<string>();
            foreach (var b in MeasureScript.Beats) { Assert.IsTrue(ids.Add(b.Id), "duplicate beat " + b.Id); Assert.IsNotEmpty(b.Text); }
            JsonValue seed;
            Assert.IsTrue(JsonValue.TryParse(MeasureScript.SeedBatch, out seed), "the seed batch is valid JSON");
            Assert.AreEqual(3, seed.GetArray("locations").Count);
            Assert.AreEqual(3, seed.GetArray("characters").Count);
            Assert.AreEqual(MeasureScript.PcId, seed.GetArray("characters")[0].GetString("id", string.Empty));
            Assert.IsTrue(seed.GetArray("characters")[0].GetBool("isPc", false));
        }

        private static MeasureRun RunOf(MeasureMode mode, params int[] wordCounts)
        {
            var run = new MeasureRun { Mode = mode, Model = "m", Provider = "p", Settings = "s" };
            for (int i = 0; i < wordCounts.Length; i++)
            {
                string text = string.Join(" ", new List<string>(new string[wordCounts[i]]).ConvertAll(delegate (string x) { return "word"; }).ToArray()) + ".";
                run.Beats.Add(new BeatResult
                {
                    Beat = MeasureScript.Beats[i],
                    Narration = text,
                    Stats = ProseStats.Measure(text),
                    ModelCalls = 2, ToolCalls = 3, PromptTokens = 1000, CompletionTokens = 100,
                });
            }
            return run;
        }

        [Test]
        public void Summary_AveragesTheScenes_AndFlagsThinOnes()
        {
            var s = MeasureReport.Summarize(RunOf(MeasureMode.Storyteller, 100, 300, 500));
            Assert.AreEqual(3, s.Scenes);
            Assert.AreEqual(300.0, s.WordsMean, 0.001);
            Assert.AreEqual(100, s.WordsMin);
            Assert.AreEqual(500, s.WordsMax);
            Assert.AreEqual(1, s.ShortScenes);
            Assert.AreEqual(2.0, s.CallsPerTurn, 0.001);
            Assert.AreEqual(1000.0, s.PromptPerTurn, 0.001);
        }

        [Test]
        public void Summary_CountsABeatWithoutNarrationAsFailed()
        {
            var run = RunOf(MeasureMode.Connector, 250);
            run.Beats.Add(new BeatResult { Beat = MeasureScript.Beats[1], Error = "the model gave no reply" });
            var s = MeasureReport.Summarize(run);
            Assert.AreEqual(1, s.Scenes);
            Assert.AreEqual(1, s.Failed);
            Assert.AreEqual(250.0, s.WordsMean, 0.001);
        }

        [Test]
        public void Report_ListsEveryBeat_AndTheComparisonNamesBothModes()
        {
            var a = RunOf(MeasureMode.Storyteller, 120, 260);
            var b = RunOf(MeasureMode.Connector, 80, 90);
            b.Remedies = false;
            string md = MeasureReport.ToMarkdown(a);
            StringAssert.Contains("Storyteller", md);
            StringAssert.Contains(MeasureScript.Beats[0].Label, md);
            StringAssert.Contains(MeasureScript.Beats[1].Label, md);
            string cmp = MeasureReport.Compare(a, b);
            StringAssert.Contains("Storyteller", cmp);
            StringAssert.Contains("Connector (baseline)", cmp);
            StringAssert.Contains("words per scene (mean)", cmp);
            string export = MeasureReport.Export(a);
            StringAssert.Contains("## Scenes", export);
            StringAssert.Contains(MeasureScript.Beats[0].Text, export);
        }
    }
}
