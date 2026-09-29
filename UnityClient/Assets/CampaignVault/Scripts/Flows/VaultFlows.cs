using System.Collections.Generic;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Flows
{
    /// <summary>
    /// Use-case flow models over raw MCP results. Everything here parses
    /// tolerantly (camelCase or PascalCase) and never throws on odd payloads:
    /// a panel shows "unavailable" instead of breaking the client.
    /// </summary>
    public sealed class DashboardQuest
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        public int OpenObjectives;
        public string Deadline = string.Empty;
        public bool Overdue;
    }

    public sealed class DashboardMember
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public bool IsPc;
        public double CurHp = -1;
        public double MaxHp = -1;
        public string Ac = string.Empty;
        public string ClassLevel = string.Empty;
        public string Location = string.Empty;
        public string Activity = string.Empty;
        public List<string> Conditions = new List<string>();
        public List<string> Equipped = new List<string>();
        public List<string> Carried = new List<string>();
        public Dictionary<string, int> Needs = new Dictionary<string, int>();
        public int MemoryCount;
        public List<string> KeyMemories = new List<string>();

        public double HpFraction
        {
            get
            {
                if (MaxHp <= 0) { return 1.0; }
                double f = CurHp / MaxHp;
                if (f < 0) { return 0; }
                if (f > 1) { return 1; }
                return f;
            }
        }

        public string HpText
        {
            get
            {
                if (MaxHp < 0) { return "—"; }
                return CurHp + " / " + MaxHp;
            }
        }
    }

    public sealed class HandoffDigest
    {
        public int FromSession;
        public bool Checkpoint;
        public string StorySoFar = string.Empty;
        public string LastSession = string.Empty;
        public List<string> OpenThreads = new List<string>();
        public List<NpcStanceRow> Npcs = new List<NpcStanceRow>();
        public string PartyIntent = string.Empty;
        public string Tone = string.Empty;
    }

    public sealed class NpcStanceRow
    {
        public string Id = string.Empty;
        public string Stance = string.Empty;
    }

    public sealed class PartyPc
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
    }

    public sealed class SessionDigest
    {
        public int SessionNumber;
        public bool Resumed;
        public string Title = string.Empty;
        public string Time = string.Empty;
        public string CampaignSlug = string.Empty;
        public string CampaignDisplay = string.Empty;
        public string System = string.Empty;
        public string Fingerprint = string.Empty;
        public string RecentDigest = string.Empty;
        public HandoffDigest Handoff;
        /// <summary>campaign.pcs from start_session: the player characters, id + name.</summary>
        public List<PartyPc> Pcs = new List<PartyPc>();
        /// <summary>campaign.companions from start_session: party companions, id + name.</summary>
        public List<PartyPc> Companions = new List<PartyPc>();
        public List<DashboardQuest> Quests = new List<DashboardQuest>();
        public List<DashboardMember> Party = new List<DashboardMember>();

        public static SessionDigest FromResult(JsonValue root)
        {
            var digest = new SessionDigest();
            if (root.Kind != JsonKind.Object) { return digest; }
            digest.SessionNumber = (int)root.GetNumber("sessionNumber", root.GetNumber("SessionNumber", 0));
            digest.Resumed = root.GetBool("resumed", root.GetBool("Resumed", false));
            digest.Title = root.GetStringAny(new[] { "title", "Title" }, string.Empty);
            digest.Time = root.GetStringAny(new[] { "time", "Time" }, string.Empty);
            digest.Fingerprint = root.GetStringAny(new[] { "partyFingerprint", "PartyFingerprint" }, string.Empty);
            digest.RecentDigest = root.GetStringAny(new[] { "recentDigest", "RecentDigest" }, string.Empty);

            var campaign = Pick(root, "campaign", "Campaign");
            if (!campaign.IsNull)
            {
                digest.CampaignSlug = campaign.GetStringAny(new[] { "slug", "Slug" }, string.Empty);
                digest.CampaignDisplay = campaign.GetStringAny(new[] { "displayName", "DisplayName", "name", "Name" }, string.Empty);
                digest.System = campaign.GetStringAny(new[] { "system", "System" }, string.Empty);
                CollectMembers(Pick(campaign, "pcs", "Pcs"), digest.Pcs);
                CollectMembers(Pick(campaign, "companions", "Companions"), digest.Companions);
            }

            var handoff = Pick(root, "handoff", "Handoff");
            if (!handoff.IsNull) { digest.Handoff = ParseHandoff(handoff); }

            foreach (var q in AsArray(Pick(root, "activeQuests", "ActiveQuests")))
            {
                var quest = new DashboardQuest
                {
                    Id = q.GetStringAny(new[] { "id", "Id", "questId", "QuestId" }, string.Empty),
                    Title = q.GetStringAny(new[] { "title", "Title" }, "Untitled"),
                    OpenObjectives = (int)q.GetNumber("openObjectives", q.GetNumber("OpenObjectives", q.GetNumber("openObjectiveCount", 0))),
                    Overdue = q.GetBool("isOverdue", q.GetBool("IsOverdue", false)),
                };
                double deadline = q.GetNumber("deadlineDay", q.GetNumber("DeadlineDay", -1));
                quest.Deadline = deadline >= 0 ? "day " + deadline : string.Empty;
                digest.Quests.Add(quest);
            }

            foreach (var p in AsArray(Pick(root, "party", "Party")))
            {
                var member = new DashboardMember
                {
                    Id = p.GetStringAny(new[] { "id", "Id" }, string.Empty),
                    Name = p.GetStringAny(new[] { "name", "Name" }, "Unknown"),
                    IsPc = p.GetBool("isPc", p.GetBool("IsPc", false)),
                    Ac = p.GetStringAny(new[] { "ac", "Ac" }, string.Empty),
                    ClassLevel = p.GetStringAny(new[] { "classLevel", "ClassLevel" }, string.Empty),
                    Location = p.GetStringAny(new[] { "locationId", "LocationId", "location", "Location" }, string.Empty),
                    Activity = p.GetStringAny(new[] { "activity", "Activity" }, string.Empty),
                    MemoryCount = (int)p.GetNumber("memoryCount", p.GetNumber("MemoryCount", 0)),
                };
                ParseHp(p.GetStringAny(new[] { "hp", "Hp" }, string.Empty), out member.CurHp, out member.MaxHp);
                double level = p.GetNumber("level", p.GetNumber("Level", -1));
                if (level >= 0 && string.IsNullOrEmpty(member.ClassLevel)) { member.ClassLevel = "Level " + level; }
                CollectStrings(p, "conditions", "Conditions", member.Conditions);
                CollectStrings(p, "equipped", "Equipped", member.Equipped);
                CollectStrings(p, "carried", "Carried", member.Carried);
                CollectStrings(p, "keyMemories", "KeyMemories", member.KeyMemories);
                var needs = Pick(p, "highNeeds", "HighNeeds");
                if (!needs.IsNull && needs.Kind == JsonKind.Object && needs.ObjectValue != null)
                {
                    foreach (var kv in needs.ObjectValue)
                    {
                        if (kv.Value.Kind == JsonKind.Number) { member.Needs[kv.Key] = (int)kv.Value.NumberValue; }
                    }
                }
                digest.Party.Add(member);
            }
            return digest;
        }

        private static void CollectMembers(JsonValue list, List<PartyPc> into)
        {
            foreach (var entry in AsArray(list))
            {
                string id = entry.GetStringAny(new[] { "id", "Id" }, string.Empty);
                if (string.IsNullOrEmpty(id)) { continue; }
                into.Add(new PartyPc { Id = id, Name = entry.GetStringAny(new[] { "name", "Name" }, id) });
            }
        }

        private static HandoffDigest ParseHandoff(JsonValue handoff)
        {
            var digest = new HandoffDigest
            {
                FromSession = (int)handoff.GetNumber("fromSession", handoff.GetNumber("FromSession", 0)),
                Checkpoint = handoff.GetBool("checkpoint", handoff.GetBool("Checkpoint", false)),
                StorySoFar = handoff.GetStringAny(new[] { "storySoFar", "StorySoFar" }, string.Empty),
                LastSession = handoff.GetStringAny(new[] { "lastSession", "LastSession" }, string.Empty),
                PartyIntent = handoff.GetStringAny(new[] { "partyIntent", "PartyIntent" }, string.Empty),
                Tone = handoff.GetStringAny(new[] { "tone", "Tone" }, string.Empty),
            };
            CollectStrings(handoff, "openThreads", "OpenThreads", digest.OpenThreads);
            foreach (var n in AsArray(Pick(handoff, "npcsInPlay", "NpcsInPlay")))
            {
                digest.Npcs.Add(new NpcStanceRow
                {
                    Id = n.GetStringAny(new[] { "id", "Id" }, string.Empty),
                    Stance = n.GetStringAny(new[] { "stance", "Stance" }, string.Empty),
                });
            }
            return digest;
        }

        private static JsonValue Pick(JsonValue obj, string camel, string pascal)
        {
            var v = obj.Get(camel);
            if (!v.IsNull) { return v; }
            return obj.Get(pascal);
        }

        private static List<JsonValue> AsArray(JsonValue v)
        {
            if (v.Kind == JsonKind.Array && v.ArrayValue != null) { return v.ArrayValue; }
            return new List<JsonValue>();
        }

        private static void CollectStrings(JsonValue obj, string camel, string pascal, List<string> into)
        {
            foreach (var v in AsArray(Pick(obj, camel, pascal)))
            {
                if (v.Kind == JsonKind.String && !string.IsNullOrEmpty(v.StringValue)) { into.Add(v.StringValue); }
            }
        }

        internal static void ParseHp(string text, out double cur, out double max)
        {
            cur = -1;
            max = -1;
            if (string.IsNullOrEmpty(text)) { return; }
            string[] parts = text.Split('/');
            if (parts.Length != 2) { return; }
            double c;
            double m;
            if (double.TryParse(parts[0].Trim(), global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out c)
                && double.TryParse(parts[1].Trim(), global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out m))
            {
                cur = c;
                max = m;
            }
        }
    }

    /// <summary>
    /// end_session handoff builder with the server's caps mirrored client-side,
    /// so the form reports overages the same way the server would reject them.
    /// </summary>
    public static class HandoffBuilder
    {
        public const int StorySoFarMax = 800;
        public const int LastSessionMax = 600;
        public const int OpenThreadsMaxCount = 6;
        public const int OpenThreadMaxLength = 120;
        public const int NpcsInPlayMaxCount = 8;
        public const int StanceMaxLength = 80;
        public const int PartyIntentMax = 200;
        public const int ToneMax = 120;

        public static List<string> Validate(
            string storySoFar, string lastSession, List<string> threads,
            List<NpcStanceRow> npcs, string partyIntent, string tone)
        {
            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(lastSession))
            {
                problems.Add("lastSession is required.");
            }
            CheckLength(problems, "storySoFar", storySoFar, StorySoFarMax);
            CheckLength(problems, "lastSession", lastSession, LastSessionMax);
            CheckLength(problems, "partyIntent", partyIntent, PartyIntentMax);
            CheckLength(problems, "tone", tone, ToneMax);
            if (threads != null)
            {
                if (threads.Count > OpenThreadsMaxCount)
                {
                    problems.Add("openThreads: " + threads.Count + " items, max " + OpenThreadsMaxCount + ".");
                }
                for (int i = 0; i < threads.Count; i++)
                {
                    if (threads[i] != null && threads[i].Length > OpenThreadMaxLength)
                    {
                        problems.Add("openThreads[" + i + "]: over by " + (threads[i].Length - OpenThreadMaxLength) + " chars.");
                    }
                }
            }
            if (npcs != null)
            {
                if (npcs.Count > NpcsInPlayMaxCount)
                {
                    problems.Add("npcsInPlay: " + npcs.Count + " entries, max " + NpcsInPlayMaxCount + ".");
                }
                for (int i = 0; i < npcs.Count; i++)
                {
                    if (npcs[i] != null && npcs[i].Stance != null && npcs[i].Stance.Length > StanceMaxLength)
                    {
                        problems.Add("npcsInPlay[" + i + "]: stance over by " + (npcs[i].Stance.Length - StanceMaxLength) + " chars.");
                    }
                }
            }
            return problems;
        }

        private static void CheckLength(List<string> problems, string field, string value, int max)
        {
            if (!string.IsNullOrEmpty(value) && value.Length > max)
            {
                problems.Add(field + ": over by " + (value.Length - max) + " chars (max " + max + ").");
            }
        }

        public static JsonValue BuildArgs(
            string campaignSlug, string storySoFar, string lastSession, List<string> threads,
            List<NpcStanceRow> npcs, string partyIntent, string tone, bool checkpoint)
        {
            var handoff = JsonValue.NewObject();
            if (!string.IsNullOrWhiteSpace(storySoFar)) { handoff.ObjectValue["storySoFar"] = JsonValue.FromString(storySoFar.Trim()); }
            handoff.ObjectValue["lastSession"] = JsonValue.FromString((lastSession ?? string.Empty).Trim());
            if (threads != null && threads.Count > 0)
            {
                var arr = JsonValue.NewArray();
                foreach (var t in threads)
                {
                    if (!string.IsNullOrWhiteSpace(t)) { arr.ArrayValue.Add(JsonValue.FromString(t.Trim())); }
                }
                if (arr.ArrayValue.Count > 0) { handoff.ObjectValue["openThreads"] = arr; }
            }
            if (npcs != null && npcs.Count > 0)
            {
                var arr = JsonValue.NewArray();
                foreach (var n in npcs)
                {
                    if (n == null || string.IsNullOrWhiteSpace(n.Id)) { continue; }
                    var row = JsonValue.NewObject();
                    row.ObjectValue["id"] = JsonValue.FromString(n.Id.Trim());
                    if (!string.IsNullOrWhiteSpace(n.Stance)) { row.ObjectValue["stance"] = JsonValue.FromString(n.Stance.Trim()); }
                    arr.ArrayValue.Add(row);
                }
                if (arr.ArrayValue.Count > 0) { handoff.ObjectValue["npcsInPlay"] = arr; }
            }
            if (!string.IsNullOrWhiteSpace(partyIntent)) { handoff.ObjectValue["partyIntent"] = JsonValue.FromString(partyIntent.Trim()); }
            if (!string.IsNullOrWhiteSpace(tone)) { handoff.ObjectValue["tone"] = JsonValue.FromString(tone.Trim()); }

            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(campaignSlug);
            args.ObjectValue["handoff"] = handoff;
            if (checkpoint) { args.ObjectValue["checkpoint"] = JsonValue.FromBool(true); }
            return args;
        }
    }
}
