using System;
using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.Flows
{
    /// <summary>
    /// "Let the DM write it": the end-of-session handoff drafted by the model from this session's chronicle.
    /// One tool-free call (OpenAiChatDriver.Brainstorm); the reply is JSON in the end_session shape, clipped
    /// to the server's caps and with people limited to NPCs the session actually showed.
    /// </summary>
    public static class HandoffDrafter
    {
        /// <summary>How much of this session's chronicle rides along, newest kept.</summary>
        public const int MaxChronicleChars = 14000;

        public static List<KeyValuePair<string, string>> Messages(SessionDigest session, IReadOnlyList<TranscriptSegment> segments, string partyLine)
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", SystemPrompt()),
                new KeyValuePair<string, string>("user", UserMessage(session, segments, partyLine)),
            };
        }

        public static string SystemPrompt()
        {
            var sb = new StringBuilder();
            sb.Append("You are the Dungeon Master closing a tabletop session. From the chronicle you are given, write the handoff the next session starts from. ");
            sb.Append("Reply with ONLY a JSON object, no markdown fence, no commentary, with these keys:\n");
            sb.Append("lastSession (string, at most ").Append(HandoffBuilder.LastSessionMax).Append(" characters: what happened this session, past tense, concrete),\n");
            sb.Append("storySoFar (string, at most ").Append(HandoffBuilder.StorySoFarMax).Append(" characters: the rolling summary of the whole campaign, folding this session into the previous one),\n");
            sb.Append("openThreads (array of at most ").Append(HandoffBuilder.OpenThreadsMaxCount).Append(" strings, each under ").Append(HandoffBuilder.OpenThreadMaxLength).Append(" characters: unresolved hooks and promises),\n");
            sb.Append("npcsInPlay (array of at most ").Append(HandoffBuilder.NpcsInPlayMaxCount).Append(" objects {id, stance}: only ids from the PEOPLE list, stance under ").Append(HandoffBuilder.StanceMaxLength).Append(" characters, how they stand toward the party now),\n");
            sb.Append("partyIntent (string, at most ").Append(HandoffBuilder.PartyIntentMax).Append(" characters: what the party means to do next, in their words),\n");
            sb.Append("tone (string, at most ").Append(HandoffBuilder.ToneMax).Append(" characters: how the next session should feel).\n");
            sb.Append("Use only what the chronicle shows; invent nothing. Plain prose, no headings, no bullet characters inside strings.");
            return sb.ToString();
        }

        public static string UserMessage(SessionDigest session, IReadOnlyList<TranscriptSegment> segments, string partyLine)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(partyLine)) { sb.Append("PARTY: ").Append(partyLine).Append('\n'); }
            var handoff = session != null ? session.Handoff : null;
            if (handoff != null)
            {
                if (handoff.StorySoFar.Length > 0) { sb.Append("STORY SO FAR (before this session): ").Append(handoff.StorySoFar).Append('\n'); }
                if (handoff.LastSession.Length > 0) { sb.Append(handoff.Checkpoint ? "EARLIER THIS SESSION: " : "LAST SESSION: ").Append(handoff.LastSession).Append('\n'); }
                if (handoff.OpenThreads.Count > 0) { sb.Append("THREADS LEFT OPEN: ").Append(string.Join("; ", handoff.OpenThreads.ToArray())).Append('\n'); }
            }
            else if (session != null && session.RecentDigest.Length > 0)
            {
                sb.Append("RECENTLY: ").Append(session.RecentDigest).Append('\n');
            }
            if (session != null && session.SeenNpcs.Count > 0)
            {
                sb.Append("PEOPLE (id — name; the only ids npcsInPlay may use):\n");
                foreach (var npc in session.SeenNpcs)
                {
                    sb.Append("- ").Append(npc.Id).Append(" — ").Append(npc.Name.Length > 0 ? npc.Name : SessionDigest.TitleFromId(npc.Id));
                    if (npc.Stance.Length > 0) { sb.Append(" (was: ").Append(npc.Stance).Append(')'); }
                    sb.Append('\n');
                }
            }
            sb.Append("\nCHRONICLE OF THIS SESSION:\n").Append(Chronicle(segments, MaxChronicleChars));
            return sb.ToString();
        }

        /// <summary>
        /// This session's play, oldest first: everything after the last recap card (the session's opening),
        /// player lines, narration and voices only. Over the budget the oldest beats drop.
        /// </summary>
        public static string Chronicle(IReadOnlyList<TranscriptSegment> segments, int maxChars)
        {
            var lines = new List<string>();
            if (segments != null)
            {
                int start = 0;
                for (int i = segments.Count - 1; i >= 0; i--) { if (segments[i].Kind == SegmentKind.Recap) { start = i + 1; break; } }
                for (int i = start; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    if (seg.Streaming || seg.Failure != null || string.IsNullOrEmpty(seg.Text)) { continue; }
                    switch (seg.Kind)
                    {
                        case SegmentKind.Player: lines.Add("PLAYER: " + seg.Text); break;
                        case SegmentKind.Narration: lines.Add(seg.Text); break;
                        case SegmentKind.NpcVoice: lines.Add(seg.Speaker + ": “" + seg.Text + "”"); break;
                        case SegmentKind.System: if (seg.Text.StartsWith("✦", StringComparison.Ordinal)) { lines.Add(seg.Text); } break;
                    }
                }
            }
            if (lines.Count == 0) { return "(nothing was played this session yet)"; }
            int total = 0;
            int first = lines.Count;
            while (first > 0 && total + lines[first - 1].Length + 2 <= maxChars) { first--; total += lines[first].Length + 2; }
            if (first == lines.Count) { first = lines.Count - 1; lines[first] = lines[first].Substring(0, Math.Min(lines[first].Length, maxChars)); }
            var sb = new StringBuilder();
            if (first > 0) { sb.Append("[earlier beats omitted]\n\n"); }
            for (int i = first; i < lines.Count; i++) { sb.Append(lines[i]).Append("\n\n"); }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The model's reply into the draft: fences and chatter around the JSON are tolerated, every field is
        /// clipped to the server's caps, and npcsInPlay keeps only people the session showed (a name is
        /// accepted in place of an id). False with a reason when no JSON object with lastSession came back.
        /// </summary>
        public static bool TryApply(string reply, SessionDigest session, HandoffDraft draft, out string error)
        {
            error = null;
            JsonValue root;
            if (!TryExtractObject(reply, out root)) { error = "the reply was not a JSON object."; return false; }
            string last = Clip(root.GetStringAny(new[] { "lastSession", "LastSession" }, string.Empty), HandoffBuilder.LastSessionMax);
            if (last.Length == 0) { error = "the reply had no lastSession."; return false; }
            draft.LastSession = last;
            draft.StorySoFar = Clip(root.GetStringAny(new[] { "storySoFar", "StorySoFar" }, string.Empty), HandoffBuilder.StorySoFarMax);
            draft.Intent = Clip(root.GetStringAny(new[] { "partyIntent", "PartyIntent" }, string.Empty), HandoffBuilder.PartyIntentMax);
            draft.Tone = Clip(root.GetStringAny(new[] { "tone", "Tone" }, string.Empty), HandoffBuilder.ToneMax);
            var threads = new List<string>();
            foreach (var t in root.GetArray("openThreads"))
            {
                if (t.Kind != JsonKind.String || string.IsNullOrWhiteSpace(t.StringValue) || threads.Count >= HandoffBuilder.OpenThreadsMaxCount) { continue; }
                threads.Add(Clip(t.StringValue, HandoffBuilder.OpenThreadMaxLength));
            }
            draft.Threads = string.Join("\n", threads.ToArray());
            var people = new List<NpcStanceRow>();
            foreach (var n in root.GetArray("npcsInPlay"))
            {
                if (n.Kind != JsonKind.Object || people.Count >= HandoffBuilder.NpcsInPlayMaxCount) { continue; }
                var seen = Resolve(session, n.GetStringAny(new[] { "id", "Id", "name", "Name" }, string.Empty));
                if (seen == null || people.Exists(delegate (NpcStanceRow r) { return r.Id == seen.Id; })) { continue; }
                people.Add(new NpcStanceRow { Id = seen.Id, Stance = Clip(n.GetStringAny(new[] { "stance", "Stance" }, string.Empty), HandoffBuilder.StanceMaxLength) });
            }
            draft.People = people;
            return true;
        }

        /// <summary>An id or a name the session showed; null for anyone else (the server would reject an unknown id anyway).</summary>
        public static SeenNpc Resolve(SessionDigest session, string idOrName)
        {
            if (session == null || string.IsNullOrWhiteSpace(idOrName)) { return null; }
            string key = idOrName.Trim();
            foreach (var npc in session.SeenNpcs) { if (string.Equals(npc.Id, key, StringComparison.OrdinalIgnoreCase)) { return npc; } }
            foreach (var npc in session.SeenNpcs)
            {
                if (npc.Name.Length > 0 && string.Equals(npc.Name, key, StringComparison.OrdinalIgnoreCase)) { return npc; }
                if (string.Equals(SessionDigest.TitleFromId(npc.Id), key, StringComparison.OrdinalIgnoreCase)) { return npc; }
            }
            return null;
        }

        private static bool TryExtractObject(string reply, out JsonValue root)
        {
            root = JsonValue.Null;
            if (string.IsNullOrEmpty(reply)) { return false; }
            int open = reply.IndexOf('{');
            int close = reply.LastIndexOf('}');
            if (open < 0 || close <= open) { return false; }
            JsonValue parsed;
            if (!JsonValue.TryParse(reply.Substring(open, close - open + 1), out parsed) || parsed.Kind != JsonKind.Object) { return false; }
            root = parsed;
            return true;
        }

        private static string Clip(string text, int max)
        {
            text = (text ?? string.Empty).Trim();
            if (max <= 0 || text.Length <= max) { return text; }
            int cut = text.LastIndexOf(' ', max - 1);
            return (cut > max / 2 ? text.Substring(0, cut) : text.Substring(0, max - 1)).TrimEnd() + "…";
        }
    }
}
