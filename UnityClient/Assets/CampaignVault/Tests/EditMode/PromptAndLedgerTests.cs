using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// N1: the model sees only the prompt, never the installer notes around
    /// it, one CAMPAIGN line, and the prompt for its ruleset. N0: token
    /// usage and the transcript export used for measurement.
    /// </summary>
    public class PromptAndLedgerTests
    {
        private string _dir;
        private GameObject _go;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vault-prompts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) { UnityEngine.Object.DestroyImmediate(_go); }
            if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); }
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        private SystemPromptProvider Provider()
        {
            _go = new GameObject("PromptTest");
            var p = _go.AddComponent<SystemPromptProvider>();
            p.ContentRoot = _dir;
            p.CampaignSlug = "scratch-vale";
            p.PartyLine = "chars/aric — Aric";
            return p;
        }

        private const string Wrapped = "# Installer notes\n\nFill in `<slug>` first. Written to be cheap per turn.\n\n```text\nYou are a Game Master.\n\nCAMPAIGN: campaignName=\"<slug>\" on every call | PCs: <chars/id — Name, ...>\n\nNARRATION\n- Write like a novel.\n```\n\nMore installer notes.\n";

        [Test]
        public void ExtractPromptBody_KeepsOnlyTheFence_WithoutTemplateCampaignLine()
        {
            string body = SystemPromptProvider.ExtractPromptBody(Wrapped);
            Assert.AreEqual("You are a Game Master.\n\nNARRATION\n- Write like a novel.", body);
        }

        [Test]
        public void ExtractPromptBody_NoFence_TakesWholeText()
        {
            Assert.AreEqual("Plain prompt.\n\nSecond paragraph.", SystemPromptProvider.ExtractPromptBody("Plain prompt.\r\n\r\n\r\nSecond paragraph.\n"));
        }

        [TestCase("recommended-system-prompt.md")]
        [TestCase("recommended-system-prompt.narrative.md")]
        public void ExtractPromptBody_RealRepoPrompts_AreClean(string file)
        {
            string path = Path.Combine(RepoRoot(), file);
            Assume.That(File.Exists(path), path + " not in this checkout");
            string body = SystemPromptProvider.ExtractPromptBody(File.ReadAllText(path));
            StringAssert.StartsWith("You are a Game Master", body);
            StringAssert.DoesNotContain("<slug>", body);
            StringAssert.DoesNotContain("```", body);
            StringAssert.DoesNotContain("Fill in", body);
            StringAssert.DoesNotContain("TOKEN_SURFACE_PLAN", body);
            Assert.IsFalse(body.Split('\n').Any(l => l.TrimStart().StartsWith("CAMPAIGN:")));
        }

        [Test]
        public void BuildSystemPrompt_OneCampaignLine_NoInstallerNotes()
        {
            File.WriteAllText(Path.Combine(_dir, "system-prompt.md"), Wrapped);
            var p = Provider();
            p.Ruleset = "Dnd5e";
            string prompt = p.BuildSystemPrompt();
            StringAssert.StartsWith("You are a Game Master.", prompt);
            StringAssert.DoesNotContain("Installer", prompt);
            StringAssert.DoesNotContain("<slug>", prompt);
            Assert.AreEqual(1, prompt.Split('\n').Count(l => l.StartsWith("CAMPAIGN:")));
            StringAssert.Contains("CAMPAIGN: campaignName=\"scratch-vale\"", prompt);
        }

        [Test]
        public void BuildSystemPrompt_PicksRulesetPrompt_FallsBackToDefault()
        {
            File.WriteAllText(Path.Combine(_dir, "system-prompt.md"), "DEFAULT PROMPT");
            File.WriteAllText(Path.Combine(_dir, "system-prompt.narrative.md"), "NARRATIVE PROMPT");
            var p = Provider();
            p.Ruleset = "Narrative";
            StringAssert.StartsWith("NARRATIVE PROMPT", p.BuildSystemPrompt());
            p.Ruleset = "Pf2e";
            StringAssert.StartsWith("DEFAULT PROMPT", p.BuildSystemPrompt());
            p.Ruleset = string.Empty;
            StringAssert.StartsWith("DEFAULT PROMPT", p.BuildSystemPrompt());
        }

        [Test]
        public void TokenUsage_ReadsOpenAiAndAnthropicShapes()
        {
            var openAi = TokenUsage.FromJson(JsonValue.Parse("{\"prompt_tokens\":1200,\"completion_tokens\":340,\"prompt_tokens_details\":{\"cached_tokens\":1024}}"));
            Assert.AreEqual(1200, openAi.Prompt);
            Assert.AreEqual(340, openAi.Completion);
            Assert.AreEqual(1024, openAi.Cached);
            var anthropic = TokenUsage.FromJson(JsonValue.Parse("{\"input_tokens\":50,\"output_tokens\":7,\"cache_read_input_tokens\":40}"));
            Assert.AreEqual(50, anthropic.Prompt);
            Assert.AreEqual(40, anthropic.Cached);
            Assert.IsTrue(TokenUsage.FromJson(JsonValue.Null).IsEmpty);
            openAi.Add(anthropic);
            Assert.AreEqual(1250, openAi.Prompt);
            Assert.AreEqual(2, openAi.Calls);
        }

        [Test]
        public void StreamAccumulator_KeepsUsageFromTheFinalChunk()
        {
            var acc = new ChatStreamAccumulator();
            acc.Feed("{\"choices\":[{\"delta\":{\"content\":\"Rain.\"}}]}");
            acc.Feed("{\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}");
            var usage = TokenUsage.FromJson(acc.ToResponse().Get("usage"));
            Assert.AreEqual(10, usage.Prompt);
            Assert.AreEqual(2, usage.Completion);
        }

        [Test]
        public void TranscriptExport_HasTurnsPlayerRollsAndDm()
        {
            var t = new TurnRecord { Player = "I search the mill.", Model = "m" };
            t.AddNarration("The wheel groans.");
            t.AddNarration("Flour lies like snow.");
            t.Rolls.Add("Perception 17 vs DC 14: Success");
            t.Usage.Add(TokenUsage.FromJson(JsonValue.Parse("{\"prompt_tokens\":100,\"completion_tokens\":20}")));
            string md = TranscriptExport.ToMarkdown("scratch-vale", "Dnd5e", new[] { t }, t.Usage);
            StringAssert.StartsWith("<!-- format: " + TranscriptExport.Format, md);
            StringAssert.Contains("## Turn 1\n<!-- usage model=m prompt=100 completion=20 cached=0 calls=1 tools=0 -->", md);
            StringAssert.Contains("### Player\n\nI search the mill.", md);
            StringAssert.Contains("### Rolls\n\n- Perception 17 vs DC 14: Success", md);
            StringAssert.Contains("### DM\n\nThe wheel groans.\n\nFlour lies like snow.", md);
        }
    }
}
