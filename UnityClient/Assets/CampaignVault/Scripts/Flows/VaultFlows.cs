using System.Collections.Generic;
using System.Text.RegularExpressions;
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
        /// <summary>The campaign's XP rule says this character has earned the next level.</summary>
        public bool LevelUpReady;

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

    /// <summary>An NPC the session has shown (a card, a scene roster, last session's handoff): what the handoff's people picker offers.</summary>
    public sealed class SeenNpc
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        /// <summary>The card's stance toward the party, when one came; the picker's starting text.</summary>
        public string Stance = string.Empty;
    }

    /// <summary>What one take_turn changed on the table (A1): the client rebuilds from the turn instead of re-reading start_session.</summary>
    public sealed class TurnTableUpdate
    {
        public bool Party;
        public bool Quests;
        public bool Time;
        /// <summary>An NPC the session had not shown before was seen (card or scene roster).</summary>
        public bool People;
        /// <summary>Player characters the fingerprint just took to 0 HP: a death save is due on their turn.</summary>
        public readonly List<string> Downed = new List<string>();
        /// <summary>Quests the turn opened that the table has no title for yet; fetched on demand with get_entity.</summary>
        public readonly List<string> NewQuestIds = new List<string>();
        public bool Any { get { return Party || Quests || Time || People; } }
    }

    public sealed class SessionDigest
    {
        /// <summary>take_turn's summary line when the clock moved: "Clock: +8h. It is now Day 3, Month 1, Year 1 — Morning, hour 7."</summary>
        private static readonly Regex ClockLine = new Regex(@"It is now (?<when>.+?), hour \d+", RegexOptions.CultureInvariant);

        /// <summary>
        /// Folds one successful take_turn into the table: HP and location from the party fingerprint, members
        /// from party[] when the DM asked for it, the clock from the summary line, level-up readiness, and
        /// quests and conditions from the changes the DM itself committed (the arguments), which cost nothing
        /// to read. A member the table has never seen is added only from party[], where its name is known.
        /// </summary>
        public TurnTableUpdate ApplyTurn(JsonValue args, JsonValue data)
        {
            var update = new TurnTableUpdate();
            if (data == null || data.Kind != JsonKind.Object) { return update; }
            string fingerprint = data.GetStringAny(new[] { "partyFingerprint", "PartyFingerprint" }, string.Empty);
            if (fingerprint.Length > 0 && fingerprint != Fingerprint)
            {
                Fingerprint = fingerprint;
                if (ApplyFingerprint(fingerprint, update.Downed)) { update.Party = true; }
            }
            foreach (var member in AsArray(Pick(data, "party", "Party")))
            {
                if (ApplyPartyMember(member)) { update.Party = true; }
            }
            foreach (var line in AsArray(Pick(data, "summary", "Summary")))
            {
                if (line.Kind != JsonKind.String) { continue; }
                var m = ClockLine.Match(line.StringValue);
                if (m.Success) { Time = m.Groups["when"].Value.Trim(); update.Time = true; }
            }
            if (NoteNpcs(data)) { update.People = true; }
            var ready = Pick(data, "levelUpReady", "LevelUpReady");
            if (ready.Kind == JsonKind.Array)
            {
                var ids = new HashSet<string>();
                foreach (var id in ready.ArrayValue) { if (id.Kind == JsonKind.String) { ids.Add(id.StringValue); } }
                foreach (var member in Party)
                {
                    bool now = ids.Contains(member.Id);
                    if (member.LevelUpReady != now) { member.LevelUpReady = now; update.Party = true; }
                }
            }
            if (args != null && args.Kind == JsonKind.Object)
            {
                var request = Pick(args, "request", "Request");
                foreach (var change in AsArray(Pick(request.IsNull ? args : request, "changes", "Changes")))
                {
                    if (change.Kind == JsonKind.Object) { ApplyChange(change, update); }
                }
            }
            return update;
        }

        public const int SeenNpcsMax = 40;

        /// <summary>
        /// Every NPC a take_turn names: npcs[] summaries, cards[] (with the stance toward the party), and the
        /// scene rosters (presentNPCs at the top level, in fullScene and in scenes[]). PCs never land here.
        /// </summary>
        private bool NoteNpcs(JsonValue data)
        {
            bool added = false;
            foreach (var npc in AsArray(Pick(data, "npcs", "Npcs")))
            {
                if (NoteNpc(npc.GetStringAny(new[] { "characterId", "CharacterId", "id", "Id" }, string.Empty), npc.GetStringAny(new[] { "name", "Name" }, string.Empty), string.Empty)) { added = true; }
            }
            foreach (var card in AsArray(Pick(data, "cards", "Cards")))
            {
                if (NoteNpc(card.GetStringAny(new[] { "id", "Id" }, string.Empty), card.GetStringAny(new[] { "name", "Name" }, string.Empty), card.GetStringAny(new[] { "stance", "Stance" }, string.Empty))) { added = true; }
            }
            if (NoteRoster(data)) { added = true; }
            if (NoteRoster(Pick(data, "fullScene", "FullScene"))) { added = true; }
            foreach (var scene in AsArray(Pick(data, "scenes", "Scenes"))) { if (NoteRoster(scene)) { added = true; } }
            return added;
        }

        private bool NoteRoster(JsonValue scene)
        {
            if (scene.Kind != JsonKind.Object) { return false; }
            bool added = false;
            foreach (var npc in AsArray(Pick(scene, "presentNPCs", "PresentNPCs")))
            {
                if (npc.GetBool("isPc", npc.GetBool("IsPc", false))) { continue; }
                if (NoteNpc(npc.GetStringAny(new[] { "id", "Id" }, string.Empty), npc.GetStringAny(new[] { "name", "Name" }, string.Empty), string.Empty)) { added = true; }
            }
            return added;
        }

        /// <summary>Records an NPC once; a later sighting fills in a name or stance the first lacked. Party members are never NPCs here.</summary>
        internal bool NoteNpc(string id, string name, string stance)
        {
            if (string.IsNullOrEmpty(id) || Member(id) != null) { return false; }
            foreach (var seen in SeenNpcs)
            {
                if (seen.Id != id) { continue; }
                if (seen.Name.Length == 0 && !string.IsNullOrEmpty(name)) { seen.Name = name; }
                if (!string.IsNullOrEmpty(stance)) { seen.Stance = stance; }
                return false;
            }
            if (SeenNpcs.Count >= SeenNpcsMax) { SeenNpcs.RemoveAt(0); }
            SeenNpcs.Add(new SeenNpc { Id = id, Name = name ?? string.Empty, Stance = stance ?? string.Empty });
            return true;
        }

        /// <summary>"chars/ari:12/20@locations/mill,chars/bo:7/9@locations/mill": HP and location for every member the table knows.</summary>
        internal bool ApplyFingerprint(string fingerprint) { return ApplyFingerprint(fingerprint, null); }

        internal bool ApplyFingerprint(string fingerprint, List<string> downed)
        {
            bool changed = false;
            foreach (string entry in fingerprint.Split(','))
            {
                int colon = entry.IndexOf(':');
                int at = entry.LastIndexOf('@');
                if (colon <= 0 || at < colon) { continue; }
                var member = Member(entry.Substring(0, colon));
                if (member == null) { continue; }
                double cur, max;
                ParseHp(entry.Substring(colon + 1, at - colon - 1), out cur, out max);
                string location = entry.Substring(at + 1);
                if (location == "?") { location = string.Empty; }
                if (member.CurHp != cur || member.MaxHp != max || member.Location != location) { changed = true; }
                if (downed != null && member.IsPc && member.CurHp > 0 && cur <= 0 && max > 0) { downed.Add(member.Name); }
                member.CurHp = cur;
                member.MaxHp = max;
                member.Location = location;
            }
            return changed;
        }

        /// <summary>One take_turn party[] entry: the character detail plus equipped/carried names.</summary>
        private bool ApplyPartyMember(JsonValue entry)
        {
            var character = Pick(entry, "character", "Character");
            if (character.IsNull || character.Kind != JsonKind.Object) { character = entry; }
            string id = character.GetStringAny(new[] { "id", "Id" }, string.Empty);
            if (id.Length == 0) { return false; }
            var member = Member(id);
            if (member == null)
            {
                member = new DashboardMember { Id = id, Name = character.GetStringAny(new[] { "name", "Name" }, id), IsPc = character.GetBool("isPc", character.GetBool("IsPc", false)) };
                Party.Add(member);
                (member.IsPc ? Pcs : Companions).Add(new PartyPc { Id = id, Name = member.Name });
            }
            double cur = character.GetNumber("currentHp", character.GetNumber("CurrentHp", member.CurHp));
            double max = character.GetNumber("maxHp", character.GetNumber("MaxHp", member.MaxHp));
            member.CurHp = cur;
            member.MaxHp = max;
            member.ClassLevel = character.GetStringAny(new[] { "classLevel", "ClassLevel" }, member.ClassLevel);
            member.Location = character.GetStringAny(new[] { "currentLocationId", "CurrentLocationId" }, member.Location);
            member.Activity = character.GetStringAny(new[] { "currentActivity", "CurrentActivity" }, member.Activity);
            var equipped = Pick(entry, "equipped", "Equipped");
            if (equipped.Kind == JsonKind.Array) { member.Equipped.Clear(); foreach (var item in equipped.ArrayValue) { member.Equipped.Add(item.Kind == JsonKind.String ? item.StringValue : item.GetStringAny(new[] { "name", "Name" }, string.Empty)); } }
            var carried = Pick(entry, "carried", "Carried");
            if (carried.Kind == JsonKind.Array) { member.Carried.Clear(); foreach (var item in carried.ArrayValue) { member.Carried.Add(item.Kind == JsonKind.String ? item.StringValue : item.GetStringAny(new[] { "name", "Name" }, string.Empty)); } }
            return true;
        }

        /// <summary>One change the DM committed: quests move or close, conditions come and go, a death is final.</summary>
        private void ApplyChange(JsonValue change, TurnTableUpdate update)
        {
            string type = change.GetString("$type", string.Empty);
            if (type == "quest_progress")
            {
                string id = change.GetStringAny(new[] { "questId", "QuestId" }, string.Empty);
                if (id.Length == 0) { return; }
                string state = change.GetStringAny(new[] { "newState", "NewState" }, string.Empty);
                bool closed = state == "Complete" || state == "Failed" || state == "Skipped";
                var quests = new List<DashboardQuest>(Quests);
                int at = quests.FindIndex(delegate (DashboardQuest q) { return q.Id == id; });
                if (closed)
                {
                    if (at < 0) { return; }
                    quests.RemoveAt(at);
                }
                else if (at < 0)
                {
                    quests.Add(new DashboardQuest { Id = id, Title = TitleFromId(id) });
                    update.NewQuestIds.Add(id);
                }
                else
                {
                    return;
                }
                Quests = quests; // a new list: the quests page refreshes by reference
                update.Quests = true;
                return;
            }
            var member = Member(change.GetStringAny(new[] { "characterId", "CharacterId" }, string.Empty));
            if (member == null) { return; }
            if (type == "status")
            {
                string status = change.GetStringAny(new[] { "status", "Status" }, string.Empty);
                if (status.Length > 0 && !member.Conditions.Contains(status)) { member.Conditions.Add(status); update.Party = true; }
            }
            else if (type == "status_remove")
            {
                string status = change.GetStringAny(new[] { "status", "Status" }, string.Empty);
                if (member.Conditions.Remove(status)) { update.Party = true; }
            }
            else if (type == "death")
            {
                bool revive = change.GetBool("revive", change.GetBool("Revive", false));
                if (revive ? member.Conditions.Remove("Dead") : AddOnce(member.Conditions, "Dead")) { update.Party = true; }
            }
        }

        private static bool AddOnce(List<string> list, string value)
        {
            if (list.Contains(value)) { return false; }
            list.Add(value);
            return true;
        }

        public DashboardMember Member(string id)
        {
            if (string.IsNullOrEmpty(id)) { return null; }
            foreach (var m in Party) { if (m.Id == id) { return m; } }
            return null;
        }

        /// <summary>"quests/find-the-miller" reads as "Find the miller" until the real title arrives.</summary>
        internal static string TitleFromId(string id)
        {
            int slash = id.LastIndexOf('/');
            string tail = (slash >= 0 ? id.Substring(slash + 1) : id).Replace('-', ' ').Replace('_', ' ').Trim();
            return tail.Length > 0 ? char.ToUpperInvariant(tail[0]) + tail.Substring(1) : id;
        }

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
        /// <summary>NPCs this session has shown, oldest first: last session's people, the opening scene's roster, then every take_turn's.</summary>
        public List<SeenNpc> SeenNpcs = new List<SeenNpc>();

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
                    LevelUpReady = p.GetBool("levelUpReady", p.GetBool("LevelUpReady", false)),
                };
                ParseHp(p.GetStringAny(new[] { "hp", "Hp" }, string.Empty), out member.CurHp, out member.MaxHp);
                double level = p.GetNumber("level", p.GetNumber("Level", -1));
                if (level >= 0 && string.IsNullOrEmpty(member.ClassLevel)) { member.ClassLevel = "Level " + level; }
                CollectStrings(p, "conditions", "Conditions", member.Conditions);
                // 5e: the death-save tally of a downed player character ("Death saves 1 of 3, 2 failed", "Stable").
                string deathSaves = p.GetStringAny(new[] { "deathSaves", "DeathSaves" }, string.Empty);
                if (deathSaves.Length > 0) { member.Conditions.Insert(0, deathSaves); }
                CollectStrings(p, "equipped", "Equipped", member.Equipped);
                CollectStrings(p, "carried", "Carried", member.Carried);
                // The roster used to be repeated under campaign.pcs/companions; party[] is the only copy now.
                (member.IsPc ? digest.Pcs : digest.Companions).Add(new PartyPc { Id = member.Id, Name = member.Name });
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
            if (digest.Handoff != null)
            {
                foreach (var n in digest.Handoff.Npcs) { digest.NoteNpc(n.Id, string.Empty, n.Stance); }
            }
            digest.NoteRoster(Pick(root, "scene", "Scene"));
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
            // "12/20 (+3 temp)": the temp hit points ride after the pair and are not part of it.
            int paren = text.IndexOf('(');
            if (paren >= 0) { text = text.Substring(0, paren); }
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
