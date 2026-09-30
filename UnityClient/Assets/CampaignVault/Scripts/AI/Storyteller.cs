using System;
using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// The two-pass turn (NARRATION_AND_CLIENT_PLAN.md, N2). The tool loop does
    /// the bookkeeping and answers DONE; a second call with no tools and a
    /// prose-only context writes the scene. The storyteller never sees tool
    /// JSON: this class turns the turn's tool results into a short plain
    /// brief, deterministically, without a model call. Pure C#.
    /// </summary>
    public static class Storyteller
    {
        public const string DoneWord = "DONE";
        public const string OocPrefix = "OOC:";
        public const int MaxBriefChars = 4800;
        public const int MaxPassages = 3;
        public const int MaxPassageChars = 6000;
        public const string ScenePrefix = "[The scene as narrated last turn]\n";
        public const string PlayerMarker = "\n\n[Player]\n";

        /// <summary>Appended to the loop's system prompt (before the CAMPAIGN line) in two-pass mode.</summary>
        public const string LoopContract =
            "# TURN CONTRACT (this client)\n" +
            "You handle the bookkeeping half of each turn. Make the tool calls the player's action needs: rolls, commits, lookups. " +
            "A separate storyteller then writes the scene from what you committed, so you don't narrate, and you don't need the dnd-narration skill: " +
            "the NARRATION section above is the storyteller's job, not yours.\n" +
            "When the state is committed (or when nothing needed committing, as in plain conversation), reply with the single word DONE.\n" +
            "The storyteller only knows what the tools returned and the one-line narrative you pass to take_turn, so put what happens in that line: " +
            "who does what, what is found, what an NPC says or decides.\n" +
            "If the player spoke out of character (a rules question, a request about the game itself), answer them in plain words, starting with " +
            OocPrefix + " and that answer is shown as is.";

        /// <summary>Single-pass (legacy) turns: the one model narrates, and this client shows rolls as cards.</summary>
        public const string SinglePassNote =
            "# THIS CLIENT\n" +
            "The player sees every roll as a card beside your text, so leave out the roll block and keep the numbers, the DCs and the words " +
            "success and failure out of the prose; show what each roll did in the fiction.";

        /// <summary>The storyteller's own instructions; the narration skill follows it.</summary>
        public const string NarrationHeader =
            "You are the storyteller at a tabletop roleplaying table. The engine has already resolved this turn: the bookkeeping is done, " +
            "the dice are rolled, and the results reach you as a brief. Your task is the scene the player reads next, written the way the guide " +
            "below shows.\n\n" +
            "Everything the brief says happened, happened; show it rather than report it. Where the brief is thin, you may add sensory texture, " +
            "weather, light, sound and smell, the body language of the people present, and dialogue that fits what the brief tells you about them. " +
            "You may not add outcomes the brief doesn't support: no extra loot, no wounds nobody rolled, no NPC who isn't there.\n\n" +
            "This client shows every roll as a card beside your text, so the prose carries no numbers, DCs, or the words success and failure. " +
            "Where the guide mentions the engine or the log, that part has already happened here.\n\n" +
            "Reply with the scene and nothing else: no headings, no notes to the player, no summary of the state.";

        /// <summary>The loop's final reply meant "I'm done, hand over to the storyteller".</summary>
        public static bool IsDone(string content)
        {
            string t = StripDecoration(content);
            return t.Length == 0 || string.Equals(t, DoneWord, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A reply that ends with a DONE line after some other text: returns the text before it.</summary>
        public static bool EndsWithDone(string content, out string before)
        {
            before = string.Empty;
            string text = (content ?? string.Empty).TrimEnd();
            int lastBreak = text.LastIndexOf('\n');
            if (lastBreak < 0) { return false; }
            if (!IsDone(text.Substring(lastBreak + 1)) || StripDecoration(text.Substring(lastBreak + 1)).Length == 0) { return false; }
            before = text.Substring(0, lastBreak).Trim();
            return true;
        }

        public static bool TryOocReply(string content, out string answer)
        {
            string t = (content ?? string.Empty).Trim().TrimStart('*', '_');
            if (t.StartsWith(OocPrefix, StringComparison.OrdinalIgnoreCase) || t.StartsWith("OOC -", StringComparison.OrdinalIgnoreCase))
            {
                answer = t.Substring(t.IndexOfAny(new[] { ':', '-' }) + 1).Trim().TrimStart('*', '_').Trim();
                return true;
            }
            answer = string.Empty;
            return false;
        }

        /// <summary>The player marked the message as out of character: "OOC …", "((…", "//…".</summary>
        public static bool IsOocPlayer(string playerText)
        {
            string t = (playerText ?? string.Empty).TrimStart();
            if (t.StartsWith("((", StringComparison.Ordinal) || t.StartsWith("//", StringComparison.Ordinal)) { return true; }
            if (t.Length >= 3 && t.Substring(0, 3).Equals("ooc", StringComparison.OrdinalIgnoreCase))
            {
                return t.Length == 3 || !char.IsLetter(t[3]);
            }
            return false;
        }

        private static string StripDecoration(string content)
        {
            return (content ?? string.Empty).Trim().Trim('*', '_', '`', '.', '!', ' ', '\n', '\r', '"', '“', '”');
        }

        /// <summary>The loop's user message: last passage first (so the loop knows what the storyteller said), then the player's line.</summary>
        public static string LoopUserMessage(string lastPassage, string playerText)
        {
            if (string.IsNullOrEmpty(lastPassage)) { return playerText; }
            return ScenePrefix + Cap(lastPassage, 2400) + PlayerMarker + playerText;
        }

        /// <summary>Older loop user messages drop the scene they carried.</summary>
        public static string StripScene(string userContent)
        {
            if (userContent == null || !userContent.StartsWith(ScenePrefix, StringComparison.Ordinal)) { return userContent; }
            int at = userContent.IndexOf(PlayerMarker, StringComparison.Ordinal);
            return at < 0 ? userContent : userContent.Substring(at + PlayerMarker.Length);
        }

        public static string NarrationSystemPrompt(string narrationSkill, string pcCard, string campaignLine)
        {
            var sb = new StringBuilder(NarrationHeader);
            if (!string.IsNullOrEmpty(narrationSkill))
            {
                sb.Append("\n\n# NARRATION GUIDE\n\n").Append(StripFrontMatter(narrationSkill).Trim());
            }
            if (!string.IsNullOrEmpty(campaignLine)) { sb.Append("\n\n# TABLE\n").Append(campaignLine.Trim()); }
            if (!string.IsNullOrEmpty(pcCard)) { sb.Append("\n\n# THE PLAYER'S CHARACTER\n").Append(pcCard.Trim()); }
            return sb.ToString();
        }

        private static string StripFrontMatter(string text)
        {
            if (!text.StartsWith("---", StringComparison.Ordinal)) { return text; }
            int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) { return text; }
            int after = text.IndexOf('\n', end + 4);
            return after < 0 ? string.Empty : text.Substring(after + 1);
        }

        /// <summary>A short "who is this" card from a get_entity character, for the storyteller.</summary>
        public static string PcCard(JsonValue entity)
        {
            if (entity == null || entity.Kind != JsonKind.Object) { return string.Empty; }
            var sb = new StringBuilder();
            string name = entity.GetString("name", string.Empty);
            string classLevel = entity.GetString("classLevel", string.Empty);
            if (name.Length > 0) { sb.Append(name); }
            if (classLevel.Length > 0) { sb.Append(sb.Length > 0 ? ", " : string.Empty).Append(classLevel); }
            if (sb.Length > 0) { sb.Append('.'); }
            var psych = entity.Get("psychology");
            Line(sb, "Appearance", entity.GetString("currentAppearance", string.Empty));
            Line(sb, "Distinctive", Join(entity.GetArray("distinctiveFeatures")));
            Line(sb, "Traits", Join(psych.GetArray("traits")));
            Line(sb, "Wants", Join(psych.GetArray("wants")));
            Line(sb, "Fears", Join(psych.GetArray("fears")));
            Line(sb, "Mood", psych.GetString("currentMood", string.Empty));
            Line(sb, "Notes", Cap(entity.GetString("notes", string.Empty), 600));
            return Cap(sb.ToString().Trim(), 1400);
        }

        private static void Line(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            sb.Append('\n').Append(label).Append(": ").Append(value.Trim());
        }

        private static string Join(List<JsonValue> values)
        {
            var parts = new List<string>();
            foreach (var v in values)
            {
                string s = v.Kind == JsonKind.String ? v.StringValue : string.Empty;
                if (!string.IsNullOrEmpty(s)) { parts.Add(s.Trim()); }
            }
            return string.Join("; ", parts.ToArray());
        }

        internal static string Cap(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) { return text ?? string.Empty; }
            int cut = text.LastIndexOf(' ', max);
            return text.Substring(0, cut > max / 2 ? cut : max) + "…";
        }
    }

    /// <summary>
    /// Collects one turn's tool calls and results and renders them as the
    /// storyteller's brief: plain sentences, no JSON, no tool names, capped.
    /// Bookkeeping-only fields (guidance, world pressure, fingerprints,
    /// retry examples) are left out; they're for the loop, not the prose.
    /// </summary>
    public sealed class TurnBrief
    {
        private readonly List<string> _committed = new List<string>();
        private readonly List<string> _engine = new List<string>();
        private readonly List<string> _rolls = new List<string>();
        private readonly List<string> _people = new List<string>();
        private readonly List<string> _places = new List<string>();
        private readonly List<string> _physical = new List<string>();
        private readonly List<string> _context = new List<string>();
        private readonly List<string> _world = new List<string>();
        private readonly List<string> _reminders = new List<string>();
        private readonly List<string> _failed = new List<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();

        public int Calls { get; private set; }

        /// <summary>One tool call: its arguments (as the model sent them) and the raw result text.</summary>
        public void Add(string toolName, JsonValue args, string resultText, bool ok)
        {
            Calls++;
            string narrative = args != null ? args.GetString("narrative", string.Empty) : string.Empty;
            if (!ok)
            {
                Push(_failed, Storyteller.Cap(TextOf(resultText), 200));
                return;
            }
            JsonValue env;
            if (!JsonValue.TryParse(resultText ?? string.Empty, out env) || env.Kind != JsonKind.Object)
            {
                return;
            }
            if (!env.GetBool("success", true))
            {
                Push(_failed, Storyteller.Cap(env.GetString("summary", env.GetString("error", "a change was rejected")), 200));
                return;
            }
            if (narrative.Length > 0) { Push(_committed, Storyteller.Cap(narrative, 400)); }
            foreach (var roll in SegmentSplitter.ExtractRolls(resultText))
            {
                var r = roll.Roll;
                Push(_rolls, r.Label + ": " + (string.IsNullOrEmpty(r.Verdict) ? r.Outcome.ToString() : Humanize(r.Verdict)));
            }
            var data = env.Get("data");
            var summaryLines = data.GetArray("summary");
            if (summaryLines.Count > 0)
            {
                foreach (var line in summaryLines) { if (line.Kind == JsonKind.String) { Push(_engine, Storyteller.Cap(line.StringValue, 240)); } }
            }
            else if (toolName != SystemPromptProvider.LoadSkillTool)
            {
                string summary = env.GetString("summary", string.Empty);
                if (summary.Length > 0 && !IsLookupNoise(toolName)) { Push(_engine, Storyteller.Cap(summary, 240)); }
            }
            foreach (var s in data.GetArray("physicalStateNudges")) { if (s.Kind == JsonKind.String) { Push(_physical, s.StringValue); } }
            foreach (var s in data.GetArray("context")) { if (s.Kind == JsonKind.String) { Push(_context, s.StringValue); } }
            string reminder = data.GetString("narrativeReminder", string.Empty);
            if (reminder.Length > 0) { Push(_reminders, Storyteller.Cap(reminder, 300)); }
            foreach (var card in data.GetArray("cards")) { Push(_people, Card(card)); }
            foreach (var scene in data.GetArray("scenes")) { Scene(scene); }
            if (data.Get("location").Kind == JsonKind.Object) { Place(data.Get("location")); }
            foreach (var npc in data.GetArray("presentNPCs")) { Push(_people, Presence(npc)); }
            var delta = data.Get("worldStateDelta");
            foreach (var e in delta.GetArray("newEvents")) { if (e.Kind == JsonKind.String) { Push(_world, e.StringValue); } }
            string time = Time(delta.Get("time"));
            if (time.Length == 0) { time = Time(data.Get("worldState").Get("time")); }
            if (time.Length > 0) { Push(_world, "Time now: " + time); }
        }

        private static bool IsLookupNoise(string toolName)
        {
            // Pure reads: their envelope summary is a count ("3 results"), not an event.
            return toolName == "lookup" || toolName == "get_entity" || toolName == "search_world" || toolName == "recall_history";
        }

        private static string Humanize(string verdict)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < verdict.Length; i++)
            {
                if (i > 0 && char.IsUpper(verdict[i]) && char.IsLower(verdict[i - 1])) { sb.Append(' '); }
                sb.Append(i > 0 ? char.ToLowerInvariant(verdict[i]) : verdict[i]);
            }
            return sb.ToString();
        }

        private static string TextOf(string resultText)
        {
            JsonValue env;
            if (JsonValue.TryParse(resultText ?? string.Empty, out env) && env.Kind == JsonKind.Object)
            {
                return env.GetString("summary", env.GetString("error", resultText));
            }
            return resultText ?? string.Empty;
        }

        private static string Card(JsonValue card)
        {
            var sb = new StringBuilder(card.GetString("name", "Someone"));
            Field(sb, "looks", card.GetString("appearance", string.Empty));
            Field(sb, "mood", card.GetString("mood", string.Empty));
            Field(sb, "traits", card.GetString("traits", string.Empty));
            Field(sb, "wants", card.GetString("wants", string.Empty));
            Field(sb, "fears", card.GetString("fears", string.Empty));
            Field(sb, "toward the party", card.GetString("stance", string.Empty));
            Field(sb, "carries", card.GetString("gear", string.Empty));
            return Storyteller.Cap(sb.ToString(), 420);
        }

        private static string Presence(JsonValue npc)
        {
            var sb = new StringBuilder(npc.GetString("name", "Someone"));
            Field(sb, "doing", npc.GetString("activity", npc.GetString("currentActivity", string.Empty)));
            Field(sb, "mood", npc.GetString("mood", npc.GetString("currentMood", string.Empty)));
            return Storyteller.Cap(sb.ToString(), 200);
        }

        private void Scene(JsonValue scene)
        {
            if (scene.Get("location").Kind == JsonKind.Object) { Place(scene.Get("location")); }
            foreach (var npc in scene.GetArray("presentNPCs")) { Push(_people, Presence(npc)); }
        }

        private void Place(JsonValue location)
        {
            var sb = new StringBuilder(location.GetString("name", "Here"));
            Field(sb, "", location.GetString("description", string.Empty));
            Field(sb, "atmosphere", location.GetString("atmosphere", string.Empty));
            Push(_places, Storyteller.Cap(sb.ToString(), 600));
        }

        private static string Time(JsonValue time)
        {
            if (time.Kind == JsonKind.String) { return time.StringValue; }
            if (time.Kind != JsonKind.Object) { return string.Empty; }
            string display = time.GetStringAny(new[] { "display", "formatted", "description" }, string.Empty);
            if (display.Length > 0) { return display; }
            string tod = time.GetString("timeOfDay", string.Empty);
            double day = time.GetNumber("day", -1);
            return (day >= 0 ? "day " + day + (tod.Length > 0 ? ", " : string.Empty) : string.Empty) + tod;
        }

        private static void Field(StringBuilder sb, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) { return; }
            sb.Append(label.Length > 0 ? " (" + label + ": " : " (").Append(value.Trim()).Append(')');
        }

        private void Push(List<string> list, string line)
        {
            string clean = (line ?? string.Empty).Trim();
            if (clean.Length == 0 || !_seen.Add(clean)) { return; }
            list.Add(clean);
        }

        /// <summary>The storyteller's final user message: the player's words, then what happened.</summary>
        public string Build(string playerText, int maxChars = Storyteller.MaxBriefChars)
        {
            var sb = new StringBuilder();
            sb.Append("The player says:\n").Append((playerText ?? string.Empty).Trim()).Append('\n');
            int headerLength = sb.Length;
            Section(sb, "What happens (as committed)", _committed);
            Section(sb, "Rolls, already shown to the player as cards", _rolls);
            Section(sb, "Engine results", _engine);
            Section(sb, "Where", _places);
            Section(sb, "Who is present or involved", _people);
            Section(sb, "Physical state to carry into the scene", _physical);
            Section(sb, "Facts this beat brings up", _context);
            Section(sb, "Meanwhile in the world", _world);
            Section(sb, "Keep in mind", _reminders);
            Section(sb, "Attempted but not applied (don't narrate these as done)", _failed);
            if (sb.Length == headerLength) { sb.Append("\nNothing was committed this turn: this is conversation or a moment in the scene.\n"); }
            sb.Append("\nWrite the scene.");
            string text = sb.ToString();
            if (text.Length > maxChars)
            {
                text = text.Substring(0, maxChars - 40) + "\n… (brief trimmed)\n\nWrite the scene.";
            }
            return text;
        }

        private static void Section(StringBuilder sb, string title, List<string> lines)
        {
            if (lines.Count == 0) { return; }
            sb.Append('\n').Append(title).Append(":\n");
            int shown = 0;
            foreach (string line in lines)
            {
                if (shown++ >= 12) { break; }
                sb.Append("- ").Append(line).Append('\n');
            }
        }
    }
}
