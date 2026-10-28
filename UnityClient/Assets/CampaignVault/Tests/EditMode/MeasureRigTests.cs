using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Measure;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// The measurer rig end to end (NARRATION_LENGTH_PLAN.md, L2 gate): a real scratch server on a throwaway DB, a scripted model, both
    /// modes over the eight beats. Skipped when no server is staged for this platform.
    /// </summary>
    [Category("Integration")]
    public class MeasureRigTests
    {
        private static readonly string Scene = string.Join(" ", Enumerable.Repeat(
            "The lantern light swings across the wet boards and the cold air smells of river mud, and you feel the planks shift under your weight as the water slaps at the pilings below.", 12).ToArray());

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Rig_PlaysBothModes_ThroughTheRealDriver()
        {
            string reports = Path.Combine(Path.GetTempPath(), "vault-measure-test-" + System.Guid.NewGuid().ToString("N"));
            var go = new GameObject("MeasureRigTest");
            try
            {
                using (var server = new ScratchServer())
                {
                    if (!server.Available) { Assert.Ignore("No embedded server staged for this platform."); }
                    yield return server.WaitHealthy();
                    Assert.IsTrue(server.Healthy, server.LogTail());
                    using (var chat = new ScriptedChat())
                    {
                        chat.Respond = delegate (JsonValue request, int index)
                        {
                            var messages = request.GetArray("messages");
                            if (messages.Count > 0 && messages[0].GetString("content", string.Empty) == FaithfulnessJudge.SystemPrompt)
                            {
                                return new ScriptedChat.Reply { Body = ScriptedChat.Prose("{\"inventions\":[{\"kind\":\"item\",\"claim\":\"a lantern\",\"quote\":\"lantern light\"}]}") };
                            }
                            bool storyteller = request.Get("tools").Kind != JsonKind.Array;
                            bool loop = messages.Count > 0 && messages[0].GetString("content", string.Empty).Contains("TURN CONTRACT");
                            if (storyteller) { return new ScriptedChat.Reply { Body = ScriptedChat.Prose(Scene) }; }
                            return new ScriptedChat.Reply
                            {
                                Body = ScriptedChat.Prose(loop ? "DONE" : "*Perception 12 vs DC 10: success*\n\n---\n\n" + Scene),
                            };
                        };
                        var source = go.AddComponent<ByokSettings>();
                        source.Profiles.Clear();
                        source.Profiles.Add(new ProviderProfile { Name = "mock", Preset = "custom", BaseUrl = chat.BaseUrl, Model = "mock-model", ApiKey = "k" });
                        source.ActiveIndex = 0;

                        var rig = new MeasureRig(null, source) { ExternalServerUrl = server.Url, ReportsFolder = reports, Judge = true };
                        MeasureRun story = null, connector = null, baseline = null;
                        string last = string.Empty;
                        yield return EditModeCoroutines.Drive(rig.Run(MeasureMode.Storyteller, 1, true, delegate (string s) { last = s; }, delegate { }, delegate (MeasureRun r) { story = r; }));
                        Assert.IsNotNull(story, last);
                        yield return EditModeCoroutines.Drive(rig.Run(MeasureMode.Connector, 1, true, delegate (string s) { last = s; }, delegate { }, delegate (MeasureRun r) { connector = r; }));
                        Assert.IsNotNull(connector, last);
                        yield return EditModeCoroutines.Drive(rig.Run(MeasureMode.Storyteller, 1, false, delegate (string s) { last = s; }, delegate { }, delegate (MeasureRun r) { baseline = r; }));
                        Assert.IsNotNull(baseline, last);

                        foreach (var run in new[] { story, connector, baseline })
                        {
                            Assert.IsEmpty(run.Failure, run.Title + ": " + run.Failure + "\n" + server.LogTail());
                            Assert.AreEqual(MeasureScript.Beats.Count, run.Beats.Count, run.Title);
                            foreach (var beat in run.Beats)
                            {
                                Assert.AreEqual(string.Empty, beat.Error, run.Title + " / " + beat.Beat.Label);
                                Assert.GreaterOrEqual(beat.Stats.Words, 200, run.Title + " / " + beat.Beat.Label);
                            }
                        }
                        Assert.AreEqual("mock-model", story.Model);

                        // Faithfulness: every storyteller scene is grounded and judged; the connector has no storyteller call to check.
                        Assert.AreEqual("mock-model", story.Judge);
                        foreach (var beat in story.Beats)
                        {
                            Assert.IsTrue(beat.Grounded, beat.Beat.Label);
                            Assert.IsTrue(beat.Judged, beat.Beat.Label + ": " + beat.JudgeError);
                            Assert.AreEqual("item", beat.Inventions.Single().Kind);
                        }
                        Assert.IsFalse(connector.Beats.Any(b => b.Grounded || b.Judged));
                        Assert.AreEqual(string.Empty, connector.Judge);
                        Assert.AreEqual(0, MeasureReport.Summarize(connector).InlineRolls, "the connector's roll block is not prose");

                        // The storyteller requests: the remedies run pins the exemplar and closes on the word target; the baseline does neither.
                        var storytellerRequests = chat.Requests.Where(r => r.Get("tools").Kind != JsonKind.Array
                            && r.GetArray("messages").Count > 0
                            && r.GetArray("messages")[0].GetString("content", string.Empty).StartsWith("You are the storyteller")).ToList();
                        Assert.AreEqual(MeasureScript.Beats.Count * 2, storytellerRequests.Count);
                        var first = storytellerRequests[0].GetArray("messages");
                        Assert.IsTrue(first.Any(m => m.GetString("content", string.Empty) == Storyteller.ExemplarScene), "remedies: exemplar pinned");
                        StringAssert.EndsWith(Storyteller.SceneRequest, first[first.Count - 1].GetString("content", string.Empty));
                        var baselineFirst = storytellerRequests[MeasureScript.Beats.Count].GetArray("messages");
                        Assert.IsFalse(baselineFirst.Any(m => m.GetString("content", string.Empty) == Storyteller.ExemplarScene), "baseline: no exemplar");
                        StringAssert.EndsWith(Storyteller.PlainSceneRequest, baselineFirst[baselineFirst.Count - 1].GetString("content", string.Empty));

                        // Connector mode plays the prompt as a web connector sees it: no client note.
                        var connectorRequest = chat.Requests.First(r => r.Get("tools").Kind == JsonKind.Array
                            && !r.GetArray("messages")[0].GetString("content", string.Empty).Contains("TURN CONTRACT"));
                        string system = connectorRequest.GetArray("messages")[0].GetString("content", string.Empty);
                        StringAssert.DoesNotContain("# THIS CLIENT", system);
                        StringAssert.DoesNotContain("TURN CONTRACT", system);

                        Assert.IsTrue(Directory.Exists(reports));
                        Assert.GreaterOrEqual(Directory.GetFiles(reports, "*.transcript.md").Length, 3, "a transcript for prose_stats.py per run");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (Directory.Exists(reports)) { try { Directory.Delete(reports, true); } catch (System.Exception) { } }
            }
        }
    }
}
