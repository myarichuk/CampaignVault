#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Diagnostics;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Measure
{
    /// <summary>
    /// Plays the fixed beat script through the real chat driver on a throwaway world (NARRATION_LENGTH_PLAN.md, L2) and measures the
    /// scenes it writes. It is isolated from the player's game on purpose: its own composition (in-memory prefs, no saved chronicle,
    /// no usage store), its own embedded server on a scratch data folder and a free port, and a seeded campaign made up for the
    /// run. The provider profile is copied in memory and never saved. Nothing of the player's campaigns, prefs or provider file is
    /// read beyond that one profile. Real model calls are made, so a run costs money.
    /// </summary>
    public sealed class MeasureRig
    {
        public const int MaxRepeats = 3;
        public const string FolderName = "MeasureRig";

        private readonly MonoBehaviour _host;
        private readonly ByokSettings _source;
        private bool _cancel;
        private OpenAiChatDriver _activeDriver;

        /// <summary>Tests: play against this already-running scratch server instead of starting (and later stopping) one.</summary>
        public string ExternalServerUrl;
        /// <summary>Tests: where reports go (defaults to <see cref="ReportsDir"/>).</summary>
        public string ReportsFolder;
        /// <summary>
        /// After each storyteller scene, ask the rules profile to list what the scene invented (one more paid call per beat).
        /// The free name check runs either way.
        /// </summary>
        public bool Judge;

        /// <summary>The run in flight; it gains a beat each time <c>changed</c> fires.</summary>
        public MeasureRun Current { get; private set; }

        public MeasureRig(MonoBehaviour host, ByokSettings source)
        {
            _host = host;
            _source = source;
        }

        /// <summary>Where the scratch server's files and the reports go.</summary>
        public static string Root { get { return Path.Combine(Application.persistentDataPath, FolderName); } }
        public static string ReportsDir { get { return Path.Combine(Root, "Reports"); } }

        public void Cancel()
        {
            _cancel = true;
            if (_activeDriver != null) { _activeDriver.Cancel(); }
        }

        /// <summary>The provider settings a run will use, one line, for the panel and the report.</summary>
        public static string DescribeSettings(ProviderProfile p)
        {
            return "temperature " + p.Temperature.ToString("0.##", CultureInfo.InvariantCulture)
                + ", max tokens " + (p.MaxTokens > 0 ? p.MaxTokens.ToString(CultureInfo.InvariantCulture) : "unset")
                + ", reasoning effort " + (string.IsNullOrEmpty(p.ReasoningEffort) ? "unset" : p.ReasoningEffort)
                + ", streaming " + (p.DisableStreaming ? "off" : "on");
        }

        /// <summary>
        /// One run: <paramref name="repeats"/> passes over the eight beats in <paramref name="mode"/>, each on a fresh seeded campaign.
        /// progress gets a status line, changed fires after every beat (the run grows in place), done gets the finished run, or null when
        /// the run could not start (the status line says why).
        /// </summary>
        public IEnumerator Run(MeasureMode mode, int repeats, bool remedies, Action<string> progress, Action changed, Action<MeasureRun> done)
        {
            _cancel = false;
            repeats = Mathf.Clamp(repeats, 1, MaxRepeats);
            var run = new MeasureRun { Mode = mode, Remedies = remedies, Repeats = repeats };
            Current = run;
            ProviderProfile profile = _source.Active;
            ProviderProfile narrator = mode == MeasureMode.Storyteller ? _source.NarratorFor(profile) : profile;
            run.Provider = profile.Name + " (" + profile.BaseUrl + ")";
            run.Model = profile.Model;
            run.Settings = DescribeSettings(profile);
            if (!ReferenceEquals(narrator, profile))
            {
                run.Narrator = narrator.Model + " via " + narrator.Name + " (" + narrator.BaseUrl + ")";
                run.NarratorSettings = DescribeSettings(narrator);
            }
            if (Judge && mode == MeasureMode.Storyteller) { run.Judge = profile.Model; }

            string reason;
            if (!_source.Validate(out reason))
            {
                progress("The provider isn't ready: " + reason);
                done(null);
                yield break;
            }
            // Storyteller mode plays two-pass even when the profile itself is single-pass, so its storyteller is checked here too.
            if (!ReferenceEquals(narrator, profile) && !ByokSettings.ValidateProfile(narrator, out reason))
            {
                progress("The storyteller profile \"" + narrator.Name + "\" isn't ready: " + reason);
                done(null);
                yield break;
            }

            var go = new GameObject("MeasureRig");
            go.SetActive(false); // no Awake yet: the bootstrap's would read the player's prefs
            var boot = go.AddComponent<VaultBootstrap>();
            boot.SkipAutostart = true;
            boot.Initialize(new MemoryPrefs());
            go.SetActive(true);
            var state = boot.State;
            try
            {
                // The rig's own copies of the active profile and its storyteller (Awake just loaded the player's file; this replaces it, and nothing saves).
                state.Byok.Profiles.Clear();
                ProviderProfile loopCopy = JsonUtility.FromJson<ProviderProfile>(JsonUtility.ToJson(profile));
                state.Byok.Profiles.Add(loopCopy);
                loopCopy.Narrator = string.Empty;
                if (!ReferenceEquals(narrator, profile))
                {
                    ProviderProfile narratorCopy = JsonUtility.FromJson<ProviderProfile>(JsonUtility.ToJson(narrator));
                    state.Byok.Profiles.Add(narratorCopy);
                    loopCopy.Narrator = ByokSettings.IdOf(narratorCopy);
                }
                state.Byok.ActiveIndex = 0;
                state.Byok.Active.SinglePass = mode == MeasureMode.Connector;
                state.Driver.SinglePassNoteOverride = mode == MeasureMode.Connector ? string.Empty : null;
                state.Driver.NarrationRemedies = remedies;
                _activeDriver = state.Driver;

                if (string.IsNullOrEmpty(ExternalServerUrl))
                {
                    progress("Starting a scratch server (the first run unpacks a copy of the server, which takes a while)…");
                    string failure = null;
                    yield return StartScratchServer(state, progress, delegate (string f) { failure = f; });
                    if (failure != null) { progress("The scratch server did not start: " + failure); done(null); yield break; }
                }
                else
                {
                    state.Config.ServerUrl = ExternalServerUrl;
                    state.Mcp.ResetSessions();
                    state.Driver.InvalidateTools();
                }

                for (int repeat = 1; repeat <= repeats && !_cancel; repeat++)
                {
                    string label = "pass " + repeat + "/" + repeats;
                    progress(label + ": creating the scratch campaign…");
                    var scenario = new VaultSmokeScenario(state, boot.Controller);
                    string name = MeasureScript.CampaignName + " " + repeat;
                    yield return scenario.Onboard(name);
                    string slug = state.CampaignSlug;
                    if (string.IsNullOrEmpty(slug)) { run.Failure = label + ": onboarding failed: " + LastLine(scenario); progress(run.Failure); break; }
                    yield return scenario.SeedBatch(slug, MeasureScript.SeedBatch);
                    if (scenario.Failed > 0) { run.Failure = label + ": seeding failed: " + LastLine(scenario); progress(run.Failure); break; }
                    yield return boot.Controller.StartSession("Measure");
                    if (state.Session == null) { run.Failure = label + ": the session did not start: " + state.SessionStatus; progress(run.Failure); break; }

                    int index = 0;
                    foreach (MeasureBeat beat in MeasureScript.Beats)
                    {
                        if (_cancel) { break; }
                        index++;
                        progress(label + ", beat " + index + "/" + MeasureScript.Beats.Count + ": " + beat.Label + "…");
                        int before = state.Driver.Turns.Count;
                        yield return boot.Controller.SendPlayerTextRoutine(beat.Text);
                        BeatResult result = Collect(state.Driver, before, beat, repeat);
                        run.Beats.Add(result);
                        if (mode == MeasureMode.Storyteller)
                        {
                            if (Judge && !_cancel) { progress(label + ", beat " + index + "/" + MeasureScript.Beats.Count + ": judging the scene…"); }
                            yield return CheckFaithfulness(state.Driver, Judge ? state.Byok.Active : null, result);
                        }
                        changed();
                    }
                    yield return boot.Controller.DeleteCampaign(slug);
                    state.Driver.ResetConversation();
                }
                string saved = Save(run, state.Driver, state.Prompts, string.IsNullOrEmpty(ReportsFolder) ? ReportsDir : ReportsFolder);
                if (run.Failure.Length == 0) { progress((_cancel ? "Stopped. " : "Done. ") + (saved.Length > 0 ? "Saved to " + saved : string.Empty)); }
                done(run);
            }
            finally
            {
                _activeDriver = null;
                if (string.IsNullOrEmpty(ExternalServerUrl))
                {
                    state.Server.StopEmbedded();
                    state.Server.WaitForStopped(20000);
                    TryDeleteDirectory(Path.Combine(Root, "CampaignData"));
                }
                if (Application.isPlaying) { UnityEngine.Object.Destroy(go); } else { UnityEngine.Object.DestroyImmediate(go); }
            }
        }

        private IEnumerator StartScratchServer(VaultAppState state, Action<string> progress, Action<string> failed)
        {
            Directory.CreateDirectory(Root);
            TryDeleteDirectory(Path.Combine(Root, "CampaignData"));
            state.Server.DataRoot = Root;
            state.Server.Port = EmbeddedServerSupport.FreePort();
            bool ok = false;
            string message = string.Empty;
            yield return state.Server.StartEmbedded(delegate (bool success, string text) { ok = success; message = text; }, progress);
            if (!ok) { failed(message); yield break; }
            failed(null);
            state.Mcp.ResetSessions();
            state.Driver.InvalidateTools();
        }

        private static BeatResult Collect(OpenAiChatDriver driver, int turnsBefore, MeasureBeat beat, int repeat)
        {
            var result = new BeatResult { Repeat = repeat, Beat = beat };
            if (driver.Turns.Count > turnsBefore)
            {
                TurnRecord t = driver.Turns[driver.Turns.Count - 1];
                result.Narration = t.Narration.ToString();
                result.Rolls.AddRange(t.Rolls);
                result.ModelCalls = t.Usage.Calls;
                result.ToolCalls = t.Tools.Count;
                result.PromptTokens = t.Usage.Prompt;
                result.CompletionTokens = t.Usage.Completion;
                result.CachedTokens = t.Usage.Cached;
                result.Cost = t.Usage.Cost;
            }
            if (!string.IsNullOrEmpty(driver.LastError)) { result.Error = driver.LastError; }
            else if (driver.Turns.Count <= turnsBefore) { result.Error = "the model gave no reply"; }
            result.Stats = ProseStats.Measure(result.Narration);
            return result;
        }

        /// <summary>
        /// Grounds the beat's scene against what its storyteller call was given, then, with a judge profile, asks it for the
        /// scene's inventions. A beat without a storyteller call (failed, or out of character) is left unchecked, not counted clean.
        /// </summary>
        private IEnumerator CheckFaithfulness(OpenAiChatDriver driver, ProviderProfile judge, BeatResult result)
        {
            if (result.Error.Length > 0 || result.Narration.Trim().Length == 0 || driver.LastNarrationMessages == null) { yield break; }
            NarrationInput input = NarrationInput.From(driver.LastNarrationMessages, driver.PcCard);
            result.Grounded = true;
            result.Ungrounded = SceneGrounding.Ungrounded(result.Narration, input.Grounds(MeasureScript.CampaignName));
            if (judge == null || _cancel) { yield break; }
            string reply = null;
            string error = null;
            TokenUsage usage = null;
            yield return driver.Complete(judge, FaithfulnessJudge.Messages(input, result.Narration), null,
                delegate (string content, TokenUsage u, string e) { reply = content; usage = u; error = e; });
            if (usage != null) { result.JudgeCost = usage.Cost; }
            List<JudgedInvention> found;
            string parseError;
            if (error != null) { result.JudgeError = error; }
            else if (FaithfulnessJudge.TryParse(reply, out found, out parseError)) { result.Judged = true; result.Inventions = found; }
            else { result.JudgeError = parseError; }
        }

        private static string LastLine(VaultSmokeScenario scenario)
        {
            return scenario.Lines.Count > 0 ? scenario.Lines[scenario.Lines.Count - 1] : "no detail";
        }

        /// <summary>Writes the report and a transcript the python script can read next to each other; returns the report path.</summary>
        private static string Save(MeasureRun run, OpenAiChatDriver driver, SystemPromptProvider prompts, string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string stamp = run.StartedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string stem = Path.Combine(folder, stamp + "-" + run.Mode.ToString().ToLowerInvariant() + (run.Remedies ? "" : "-baseline"));
                File.WriteAllText(stem + ".md", MeasureReport.Export(run));
                File.WriteAllText(stem + ".transcript.md", TranscriptExport.ToMarkdown(MeasureScript.CampaignName, prompts.Ruleset, driver.Turns, driver.SessionUsage));
                return stem + ".md";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Measure] could not save the report: " + e.Message);
                return string.Empty;
            }
        }

        /// <summary>Removes the scratch server's unpacked copy and reports (the panel's "clear" button).</summary>
        public static void ClearFiles() { TryDeleteDirectory(Root); }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) { Directory.Delete(path, true); } }
            catch (Exception) { /* a server still shutting down holds files; the next run clears them */ }
        }
    }
}
#endif
