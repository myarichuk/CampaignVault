using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Measure;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The debug narration measurer's pure parts (NARRATION_LENGTH_PLAN.md, L1): the prose statistics, the script, the faithfulness checks and the report.</summary>
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
            var tokens = new[] { "MeasurePage", "MeasureRig", "MeasureScript", "MeasureReport", "MeasureRun", "MeasureBeat", "MeasureMode", "ProseStats", "MeasureTab", "SceneGrounding", "FaithfulnessJudge", "JudgedInvention", "NarrationInput" };
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

        // ---- faithfulness: names with no grounds, and the judge ----

        private const string Grounds = "The player says:\nI ask the miller who's been on the road.\n\nWho is present or involved:\n- Hesper Dunmore (mood: wary)\n"
            + "Where:\n- Millbrook (A small town of timber and thatch around a mill pond)\n";

        [Test]
        public void Grounding_FlagsAnInventedName_AndOnlyThat()
        {
            string scene = "Hesper wipes her hands on her apron. “Ask Tomas Reed,” she says. “He keeps the ferry below Millbrook.”\n\n"
                + "The pond is still. You nod.";
            CollectionAssert.AreEqual(new[] { "Tomas Reed" }, SceneGrounding.Ungrounded(scene, Grounds));
        }

        [Test]
        public void Grounding_IgnoresSentenceOpeners_ButNotANameUsedAsOne()
        {
            Assert.IsEmpty(SceneGrounding.Ungrounded("Somewhere a dog barks. Rain starts. Behind you, the door creaks.", Grounds),
                "a capital that only ever opens a sentence is grammar");
            CollectionAssert.AreEqual(new[] { "Varn" }, SceneGrounding.Ungrounded("Varn steps out of the dark. You watch Varn go.", Grounds),
                "the same word mid-sentence elsewhere shows it is a name");
        }

        [Test]
        public void Grounding_ReadsPossessivesAndTitles()
        {
            Assert.IsEmpty(SceneGrounding.Ungrounded("You catch Mistress Hesper's eye across Millbrook's square.", Grounds),
                "a title before a grounded name, and possessives, are grounded");
            CollectionAssert.AreEqual(new[] { "Captain Orrin" }, SceneGrounding.Ungrounded("You think of old Captain Orrin and his boat.", Grounds));
            Assert.IsEmpty(SceneGrounding.Ungrounded("The stranger bows to the Lady and the Captain.", Grounds), "titles alone name no one");
        }

        [Test]
        public void Grounding_SplitsRunsAtPunctuation()
        {
            CollectionAssert.AreEqual(new[] { "Bram", "Ilsa" }, SceneGrounding.Ungrounded("You signal to Hesper, Bram, and Ilsa.", Grounds));
        }

        private static JsonValue Msg(string role, string content)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString(role);
            m.ObjectValue["content"] = JsonValue.FromString(content);
            return m;
        }

        [Test]
        public void NarrationInput_LeavesTheGuideAndTheExemplarOut()
        {
            var messages = JsonValue.NewArray();
            messages.ArrayValue.Add(Msg("system", "You are the storyteller… worked example: Mirelle the smith"));
            messages.ArrayValue.Add(Msg("user", "The player says:\n" + Storyteller.ExemplarPlayer));
            messages.ArrayValue.Add(Msg("assistant", Storyteller.ExemplarScene));
            messages.ArrayValue.Add(Msg("user", "The player says:\nI knock."));
            messages.ArrayValue.Add(Msg("assistant", "Bram opens the door."));
            messages.ArrayValue.Add(Msg("user", Grounds));
            var input = NarrationInput.From(messages, "Wren Ashdown, a lean ranger");

            Assert.AreEqual(Grounds, input.Brief);
            StringAssert.Contains("Bram opens the door.", input.Earlier);
            StringAssert.DoesNotContain("ferryman", input.Earlier, "the pinned exemplar is style, not world");
            CollectionAssert.AreEqual(new[] { "Mirelle" }, SceneGrounding.Ungrounded("Wren takes a horseshoe from Mirelle while Bram watches.", input.Grounds(string.Empty)),
                "a name copied out of the guide's examples is an invention");
        }

        [Test]
        public void Judge_ParsesTheList_ToleratingAFenceAndUnknownKinds()
        {
            List<JudgedInvention> found;
            string error;
            string reply = "Here you go:\n```json\n{\"inventions\":[{\"kind\":\"Item\",\"claim\":\"a dagger on the table\",\"quote\":\"a dagger glints\"},"
                + "{\"kind\":\"vibes\",\"claim\":\"an omen\"},{\"kind\":\"entity\"}]}\n```";
            Assert.IsTrue(FaithfulnessJudge.TryParse(reply, out found, out error), error);
            Assert.AreEqual(2, found.Count, "an entry with neither claim nor quote is dropped");
            Assert.AreEqual("item", found[0].Kind);
            Assert.AreEqual("a dagger glints", found[0].Quote);
            Assert.AreEqual("other", found[1].Kind);

            Assert.IsTrue(FaithfulnessJudge.TryParse("{\"inventions\":[]}", out found, out error));
            Assert.IsEmpty(found, "a clean scene");
            Assert.IsFalse(FaithfulnessJudge.TryParse("The scene looks faithful to me.", out found, out error));
            Assert.IsFalse(FaithfulnessJudge.TryParse("{\"verdict\":\"fine\"}", out found, out error), "no list is not a clean scene");
        }

        [Test]
        public void Judge_SeesTheBriefTheEarlierScenesAndTheScene()
        {
            var input = new NarrationInput { Brief = "BRIEF-TEXT", Earlier = "EARLIER-TEXT", PcCard = "PC-CARD" };
            var messages = FaithfulnessJudge.Messages(input, "SCENE-TEXT").ArrayValue;
            Assert.AreEqual(2, messages.Count);
            Assert.AreEqual(FaithfulnessJudge.SystemPrompt, messages[0].GetString("content", null));
            string user = messages[1].GetString("content", string.Empty);
            foreach (string part in new[] { "BRIEF-TEXT", "EARLIER-TEXT", "PC-CARD", "SCENE-TEXT" }) { StringAssert.Contains(part, user); }
            Assert.Less(user.IndexOf("BRIEF-TEXT"), user.IndexOf("SCENE-TEXT"), "the scene comes last");
        }

        [Test]
        public void Report_CountsFaithfulness_AndNeverShowsAMissingCheckAsZero()
        {
            var a = RunOf(MeasureMode.Storyteller, 300, 300, 300);
            a.Narrator = "small-model via Prose (http://x)";
            a.Judge = "big-model";
            a.Beats[0].Grounded = true;
            a.Beats[0].Ungrounded.AddRange(new[] { "Tomas Reed", "Varn" });
            a.Beats[0].Judged = true;
            a.Beats[0].Inventions.Add(new JudgedInvention { Kind = "item", Claim = "a dagger", Quote = "a dagger glints" });
            a.Beats[0].JudgeCost = 0.002;
            a.Beats[1].Grounded = true;
            a.Beats[1].Judged = true;
            a.Beats[2].Grounded = true;
            a.Beats[2].JudgeError = "the judge's reply was not JSON";

            var s = MeasureReport.Summarize(a);
            Assert.AreEqual(3, s.GroundedScenes);
            Assert.AreEqual(2, s.UngroundedNames);
            Assert.AreEqual(1, s.ScenesWithUngrounded);
            Assert.AreEqual(2, s.JudgedScenes);
            Assert.AreEqual(1, s.Inventions);
            Assert.AreEqual(1, s.JudgeFailures);
            Assert.AreEqual(1, s.InventionsByKind["item"]);
            Assert.AreEqual(0.002, s.JudgeCost, 1e-9);
            Assert.AreEqual(0.0, s.CostTotal, 1e-9, "judging is not what play costs");

            string md = MeasureReport.ToMarkdown(a);
            StringAssert.Contains("scenes by small-model", md);
            StringAssert.Contains("Faithfulness judge: big-model", md);
            StringAssert.Contains("Ungrounded names: 2 in 1 of 3 scenes", md);
            StringAssert.Contains("Judged inventions: 1 in 1 of 2 scenes (item 1); the judge failed on 1", md);
            string export = MeasureReport.Export(a);
            StringAssert.Contains("**Ungrounded names:** Tomas Reed, Varn", export);
            StringAssert.Contains("- item: a dagger (“a dagger glints”)", export);
            StringAssert.Contains("**Judge failed:** the judge's reply was not JSON", export);

            var b = RunOf(MeasureMode.Connector, 300);
            string cmp = MeasureReport.Compare(a, b);
            StringAssert.Contains("| ungrounded names | 2 | – |", cmp, "the connector run was never grounded: a dash, not a clean zero");
            StringAssert.Contains("| judged inventions | 1 | – |", cmp);
            StringAssert.DoesNotContain("ungrounded", MeasureReport.SummaryLines(MeasureReport.Summarize(b)).ToLowerInvariant());
        }
    }
}
