using System.Collections;
using System.Collections.Generic;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// The onboarding party step: the characters built for the campaign being set up (kept in
    /// <see cref="OnboardingState.Party"/> so the builder round trips and the step's page rebuilds lose nothing), and
    /// the answer that tells the server who they are.
    /// </summary>
    public sealed partial class VaultController
    {
        /// <summary>Opens the builder for a new party member, or for the one with this id.</summary>
        public void OpenPartyBuilder(string editId, string kind = "pc") { _s.RequestPartyBuilder(editId, kind); }

        public void SetPartyLevel(int level)
        {
            var ob = _s.Onboarding;
            int max = MaxBuilderLevelFor(RulesetOf(ob.System));
            level = level < 1 ? 1 : level > max ? max : level;
            if (ob.PartyLevel == level) { return; }
            ob.PartyLevel = level;
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>A character the builder just saved joins (or updates) the party.</summary>
        private void RecordPartyMember(BuilderState b, string id)
        {
            if (id.Length == 0) { return; }
            var ob = _s.Onboarding;
            var member = ob.Party.Find(delegate (PartyMember m) { return m.Id == id; });
            // A reviewed DM draft: its card becomes the saved character, in the same place.
            if (member == null && b.PendingKey.Length > 0)
            {
                member = ob.Party.Find(delegate (PartyMember m) { return m.Pending && m.Id == b.PendingKey; });
                if (member != null) { member.Id = id; member.Pending = false; member.Issues.Clear(); }
            }
            b.PendingKey = string.Empty;
            if (!HasPendingDrafts(ob)) { ob.DraftError = string.Empty; }
            if (member == null)
            {
                member = new PartyMember { Id = id };
                ob.Party.Add(member);
            }
            member.Kind = b.Draft.Kind;
            member.Draft = CharacterDraft.FromJson(b.Draft.ToJson());
            member.Name = TextSanitizer.Clean(b.Preview != null && b.Preview.Name.Length > 0 ? b.Preview.Name : b.Draft.Name, 80);
            member.ClassLine = TextSanitizer.Clean(b.Preview != null ? b.Preview.ClassLine : string.Empty, 80);
            member.Level = b.Preview != null && b.Preview.Level > 0 ? b.Preview.Level : b.Draft.Level;
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>The party step's answer, in the form the server validates.</summary>
        public static string PartyAnswer(string mode, int level, IList<PartyMember> built)
        {
            var answer = JsonValue.NewObject();
            answer.ObjectValue["mode"] = JsonValue.FromString(mode);
            answer.ObjectValue["level"] = JsonValue.FromNumber(level);
            var characters = JsonValue.NewArray();
            var companions = JsonValue.NewArray();
            if (built != null)
            {
                foreach (var m in built)
                {
                    (m.Kind == "companion" ? companions : characters).ArrayValue.Add(JsonValue.FromString(m.Id));
                }
            }
            answer.ObjectValue["characterIds"] = characters;
            answer.ObjectValue["companionIds"] = companions;
            return answer.ToJson();
        }

        /// <summary>
        /// Sends the answer. The characters built so far go with it when the mode uses them; the table builds its own
        /// otherwise (anything built in the meantime stays in the campaign as a player character).
        /// </summary>
        public void SubmitParty(string mode)
        {
            var ob = _s.Onboarding;
            if (mode != OnboardingState.PartyBuildAtTable && HasPendingDrafts(ob))
            {
                ob.DraftError = "Review or discard the DM's drafts first.";
                _s.Notify(StateArea.Onboarding);
                return;
            }
            var built = mode == OnboardingState.PartyBuildAtTable ? null : ob.Party;
            Run(SubmitOnboardingAnswer(PartyAnswer(mode, ob.PartyLevel, built)));
        }
        // ---- the DM drafts companions (4.4) ----

        public static bool HasPendingDrafts(OnboardingState ob)
        {
            return ob.Party.Exists(delegate (PartyMember m) { return m.Pending; });
        }

        /// <summary>The player's own characters: what the drafted companions are written around.</summary>
        public static bool HasBuiltPc(OnboardingState ob)
        {
            return ob.Party.Exists(delegate (PartyMember m) { return !m.Pending && m.Kind != "companion"; });
        }

        private int _draftSerial;

        public void DraftCompanions() { Run(DraftCompanionsRoutine()); }

        /// <summary>
        /// ONE model call drafts the party's companions, in the setup conversation; each draft is then previewed by the
        /// server and shown as a card to review. Earlier drafts not reviewed yet are replaced.
        /// </summary>
        public IEnumerator DraftCompanionsRoutine()
        {
            var ob = _s.Onboarding;
            if (ob.Drafting || !HasBuiltPc(ob)) { yield break; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                ob.DraftError = "Drafting needs a working AI provider: " + notReady;
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.Drafting = true;
            ob.DraftError = string.Empty;
            _s.Notify(StateArea.Onboarding);
            try
            {
                CompanionKit kit = null;
                yield return LoadCompanionKit(delegate (CompanionKit k) { kit = k; });
                if (kit.Error.Length > 0) { ob.DraftError = kit.Error; yield break; }
                var messages = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("system", PartyDrafter.SystemPrompt(RulesetOf(ob.System), kit.Schema, kit.Templates, ob.Party,
                        ob.PartyLevel, MaxBuilderLevelFor(RulesetOf(ob.System)), ob.Answers)),
                };
                messages.AddRange(OnboardingBrainstorm.ModelMessages(ob.BrainstormChat, OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars)));
                messages.Add(new KeyValuePair<string, string>("user", PartyDrafter.Instruction()));
                string reply = null;
                string error = null;
                yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
                if (error != null) { ob.DraftError = TextSanitizer.Clean(error, 400); yield break; }
                yield return ApplyCompanionDrafts(reply, kit);
            }
            finally
            {
                ob.Drafting = false;
                _s.Notify(StateArea.Onboarding);
            }
        }

        /// <summary>
        /// The model's reply as cards to review: parsed against the companion stat block, each previewed by the server
        /// (its errors and warnings go on the card). Split from the call so tests can feed a reply. Nothing is saved.
        /// </summary>
        public IEnumerator ApplyCompanionDrafts(string reply, CompanionKit kit = null)
        {
            var ob = _s.Onboarding;
            if (kit == null) { yield return LoadCompanionKit(delegate (CompanionKit k) { kit = k; }); }
            if (kit.Error.Length > 0) { ob.DraftError = kit.Error; _s.Notify(StateArea.Onboarding); yield break; }
            var drafts = new List<DraftedCompanion>();
            string parseError;
            if (!PartyDrafter.Parse(reply, kit.Schema, ob.PartyLevel, MaxBuilderLevelFor(RulesetOf(ob.System)), drafts, out parseError))
            {
                ob.DraftError = parseError;
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.Party.RemoveAll(delegate (PartyMember m) { return m.Pending; });
            ob.BrainstormChat.Add(new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, BuilderAdvisor.Marker("companions", "DM drafts")));
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", TextSanitizer.Clean(PartyDrafter.ChatSummary(drafts), OnboardingBrainstorm.MaxReplyChars)));
            foreach (var d in drafts)
            {
                // Placeholder ids never repeat, so a card from an earlier draft is never mistaken for a new one.
                var member = new PartyMember { Id = "draft-" + (++_draftSerial), Kind = "companion", Pending = true, Draft = d.Draft, Name = d.Draft.Name, Level = d.Draft.Level };
                member.Issues.AddRange(d.Notes);
                McpOutcome<ToolPayload> result = null;
                yield return CallBuilder(ob.Slug, d.Draft, "preview", null, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result == null || !result.Ok) { member.Issues.Add("Preview failed: " + (result != null ? result.ErrorMessage : "no response")); }
                else
                {
                    foreach (var e in result.Data.Data.GetArray("errors")) { member.Issues.Add("Fix: " + BuilderIssue.Parse(e).Message); }
                    foreach (var w in result.Data.Data.GetArray("warnings")) { member.Issues.Add("Note: " + BuilderIssue.Parse(w).Message); }
                }
                ob.Party.Add(member);
            }
            ob.DraftError = string.Empty;
            _s.Notify(StateArea.Onboarding);
        }

        public void DiscardDrafts()
        {
            var ob = _s.Onboarding;
            ob.Party.RemoveAll(delegate (PartyMember m) { return m.Pending; });
            ob.DraftError = string.Empty;
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>The companion recipe's stat block (fields, ranges) and templates, for the campaign being set up.</summary>
        public sealed class CompanionKit
        {
            public StatBlockSchema Schema;
            public readonly List<BuilderOption> Templates = new List<BuilderOption>();
            public string Error = string.Empty;
        }

        private IEnumerator LoadCompanionKit(System.Action<CompanionKit> done)
        {
            var ob = _s.Onboarding;
            var kit = new CompanionKit();
            var probe = new CharacterDraft { Kind = "companion", Level = ob.PartyLevel, PartyLevel = ob.PartyLevel };
            McpOutcome<ToolPayload> steps = null;
            yield return CallBuilder(ob.Slug, probe, "steps", null, delegate (McpOutcome<ToolPayload> o) { steps = o; });
            if (steps == null || !steps.Ok)
            {
                kit.Error = "Couldn't load the companion stat block: " + (steps != null ? steps.ErrorMessage : "no response");
                done(kit);
                yield break;
            }
            BuilderStep block = null;
            foreach (var s in steps.Data.Data.GetArray("steps"))
            {
                var step = BuilderStep.Parse(s);
                if (step.Key == PartyDrafter.StatBlockKey) { block = step; }
            }
            foreach (var s in steps.Data.Data.GetArray("statBlocks"))
            {
                var schema = StatBlockSchema.Parse(s);
                if (block != null && string.Equals(schema.Name, block.Schema, System.StringComparison.OrdinalIgnoreCase)) { kit.Schema = schema; }
            }
            if (block == null || kit.Schema == null)
            {
                kit.Error = "This game system has no companion stat block to draft into.";
                done(kit);
                yield break;
            }
            if (block.Source.Length > 0)
            {
                McpOutcome<ToolPayload> options = null;
                yield return CallBuilder(ob.Slug, probe, "options", block.Key, delegate (McpOutcome<ToolPayload> o) { options = o; });
                kit.Templates.AddRange(ReadOptions(options).Options);
            }
            done(kit);
        }
    }
}
