using System.Collections;
using System.Collections.Generic;
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
        public void OpenPartyBuilder(string editId) { _s.RequestPartyBuilder(editId); }

        public void SetPartyLevel(int level)
        {
            var ob = _s.Onboarding;
            level = level < 1 ? 1 : level > MaxBuilderLevel ? MaxBuilderLevel : level;
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
            var built = mode == OnboardingState.PartyBuildAtTable ? null : ob.Party;
            Run(SubmitOnboardingAnswer(PartyAnswer(mode, ob.PartyLevel, built)));
        }
    }
}
