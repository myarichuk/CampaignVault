#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Measure
{
    /// <summary>
    /// What a scene was allowed to draw on: the storyteller call's own messages, minus the parts that are style rather than
    /// world (the system prompt with the narration guide and its worked examples, and the pinned exemplar exchange). A name
    /// copied out of a guide example is exactly the kind of invention worth catching, so the guide never counts as grounds.
    /// </summary>
    public sealed class NarrationInput
    {
        /// <summary>The storyteller's final user message: the player's line and the brief.</summary>
        public string Brief = string.Empty;
        /// <summary>Earlier scenes replayed to the storyteller, oldest first (each with the player line it answered).</summary>
        public string Earlier = string.Empty;
        /// <summary>Who the player's character is, as the storyteller got it.</summary>
        public string PcCard = string.Empty;

        /// <summary>Everything a scene may name: brief, earlier scenes, the character, and any extra known world text.</summary>
        public string Grounds(string extra)
        {
            return Brief + "\n" + Earlier + "\n" + PcCard + "\n" + (extra ?? string.Empty);
        }

        public static NarrationInput From(JsonValue messages, string pcCard)
        {
            var input = new NarrationInput { PcCard = pcCard ?? string.Empty };
            if (messages == null || messages.Kind != JsonKind.Array) { return input; }
            var list = messages.ArrayValue;
            int last = list.Count - 1;
            var earlier = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                string role = list[i].GetString("role", string.Empty);
                string content = list[i].GetString("content", string.Empty);
                if (role == "system") { continue; }
                if (i == last && role == "user") { input.Brief = content; continue; }
                if (content == Storyteller.ExemplarScene || content.EndsWith(Storyteller.ExemplarPlayer, StringComparison.Ordinal)) { continue; }
                earlier.Append(content).Append("\n\n");
            }
            input.Earlier = earlier.ToString().Trim();
            return input;
        }
    }

    /// <summary>
    /// The free half of the faithfulness check: proper names in a scene that appear nowhere in what the storyteller was given.
    /// A name with no grounds is an NPC, a place or a faction the narrator made up, and the next turn's tool loop reads the
    /// scene, so it either contradicts the player or has to make the thing real. It cannot see an unnamed invention
    /// ("a dagger glints on the table"); that is the judge's job. It errs toward silence: a name that appears only once, as
    /// the first word of a sentence, reads like any other capitalized opener ("Rain", "Dust") and is not reported.
    /// </summary>
    public static class SceneGrounding
    {
        private static readonly Regex Word = new Regex(@"\p{L}[\p{L}'’\-]*", RegexOptions.Compiled);

        /// <summary>Capitalized for grammar, not because they name anything; never reported.</summary>
        private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.Ordinal)
        {
            "I", "A", "An", "The", "This", "That", "These", "Those", "You", "Your", "Yours", "He", "His", "Him", "She", "Her", "Hers",
            "It", "Its", "We", "Our", "They", "Their", "Them", "My", "Me", "Mine", "Who", "What", "When", "Where", "Why", "How",
            "There", "Here", "Then", "Now", "And", "But", "Or", "Nor", "So", "Yet", "For", "If", "As", "At", "By", "In", "On", "Of",
            "To", "Up", "No", "Not", "Yes", "Oh", "Ah", "All", "Some", "Every", "Each", "One", "Two", "Three", "Somewhere", "Something",
            "Someone", "Nothing", "Nobody", "Behind", "Beyond", "Above", "Below", "Before", "After", "Inside", "Outside", "Still",
            "Only", "Just", "Even", "Again", "Once", "Too", "Well", "Good", "Mind",
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        };

        /// <summary>Words that ride in front of a name; a run made of these alone names no one.</summary>
        private static readonly HashSet<string> Titles = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mister", "Master", "Mistress", "Madam", "Lady", "Lord", "Sir", "Dame", "Captain", "Sergeant", "Old", "Young", "Father",
            "Mother", "Brother", "Sister", "Uncle", "Aunt", "Saint", "St", "Goodman", "Goodwife", "King", "Queen", "Prince",
            "Princess", "Duke", "Baron", "Count", "Elder", "Sheriff", "Mayor",
        };

        /// <summary>The scene's names that have no grounds, each once, in the order they first appear.</summary>
        public static List<string> Ungrounded(string scene, string grounds)
        {
            var known = Vocabulary(grounds);
            var found = new List<string>();
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Token> tokens = Tokenize(scene ?? string.Empty);
            // A capital that also shows up mid-sentence somewhere in the scene is a name even where it opens a sentence.
            var midSentence = new HashSet<string>(StringComparer.Ordinal);
            foreach (Token t in tokens) { if (t.Capital && !t.SentenceStart) { midSentence.Add(t.Bare); } }
            // A word written in lower case anywhere is vocabulary, so as a sentence opener ("“Ask Tomas Reed,”") it is not part of the name.
            var lowerSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Token t in tokens) { if (!t.Capital) { lowerSeen.Add(t.Bare); } }
            foreach (Token t in Tokenize(grounds ?? string.Empty)) { if (!t.Capital) { lowerSeen.Add(t.Bare); } }

            int i = 0;
            while (i < tokens.Count)
            {
                if (!tokens[i].Capital) { i++; continue; }
                int end = i;
                while (end + 1 < tokens.Count && tokens[end + 1].Capital && tokens[end + 1].Adjacent) { end++; }
                var run = new List<Token>();
                for (int k = i; k <= end; k++)
                {
                    Token t = tokens[k];
                    // A sentence opener is grammar when it stands alone or is a lower-case word elsewhere, unless the scene uses it as a name.
                    bool opener = t.SentenceStart && k == i && !midSentence.Contains(t.Bare)
                        && (end == i || lowerSeen.Contains(t.Bare.ToLowerInvariant()));
                    if (Common.Contains(t.Bare) || opener) { continue; }
                    run.Add(t);
                }
                i = end + 1;
                if (run.Count == 0) { continue; }
                bool named = false, grounded = false;
                foreach (Token t in run)
                {
                    if (Titles.Contains(t.Bare)) { continue; }
                    named = true;
                    if (known.Contains(t.Bare.ToLowerInvariant())) { grounded = true; break; }
                }
                if (!named || grounded) { continue; }
                string name = Join(run);
                if (reported.Add(name)) { found.Add(name); }
            }
            return found;
        }

        private struct Token
        {
            public string Bare;
            public bool Capital;
            public bool SentenceStart;
            /// <summary>Only spaces separate it from the token before (so "Hesper Dunmore" is one run, "Hesper, Bram" is two).</summary>
            public bool Adjacent;
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int previousEnd = 0;
            bool first = true;
            foreach (Match m in Word.Matches(text))
            {
                string gap = text.Substring(previousEnd, m.Index - previousEnd);
                string bare = Bare(m.Value);
                tokens.Add(new Token
                {
                    Bare = bare,
                    Capital = bare.Length > 0 && char.IsUpper(bare[0]),
                    SentenceStart = first || OpensSentence(gap),
                    Adjacent = !first && gap.Trim(' ').Length == 0,
                });
                previousEnd = m.Index + m.Length;
                first = false;
            }
            return tokens;
        }

        /// <summary>The gap before a word ends a sentence, a line, or opens a quotation.</summary>
        private static bool OpensSentence(string gap)
        {
            foreach (char c in gap)
            {
                if (c == '.' || c == '!' || c == '?' || c == '…' || c == ':' || c == ';' || c == '\n' || c == '“' || c == '"' || c == '‘' || c == '—' || c == '(') { return true; }
            }
            return false;
        }

        /// <summary>The word without a possessive ending or edge punctuation ("Hesper's" → "Hesper").</summary>
        private static string Bare(string word)
        {
            string w = word.Trim('\'', '’', '-');
            if (w.EndsWith("'s", StringComparison.Ordinal) || w.EndsWith("’s", StringComparison.Ordinal)) { w = w.Substring(0, w.Length - 2); }
            return w;
        }

        private static HashSet<string> Vocabulary(string grounds)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Word.Matches(grounds ?? string.Empty))
            {
                string bare = Bare(m.Value).ToLowerInvariant();
                words.Add(bare);
                // "river-road" in an id grounds "River" and "Road" in the prose.
                foreach (string part in bare.Split('-')) { if (part.Length > 0) { words.Add(part); } }
            }
            return words;
        }

        private static string Join(List<Token> run)
        {
            var parts = new string[run.Count];
            for (int i = 0; i < run.Count; i++) { parts[i] = run[i].Bare; }
            return string.Join(" ", parts);
        }
    }

    /// <summary>One claim the judge found beyond what the storyteller was given.</summary>
    public sealed class JudgedInvention
    {
        public string Kind = "other";
        public string Claim = string.Empty;
        public string Quote = string.Empty;
    }

    /// <summary>
    /// The paid half of the faithfulness check: a second model reads the brief, the earlier scenes and the scene, and lists
    /// what the scene added that the narration header forbids (people, objects, outcomes, wounds, contradictions), leaving
    /// out what it allows (texture, body language, dialogue that colors the brief). The measurer runs it on the rules profile:
    /// a narrator judging its own scene grades its own homework.
    /// </summary>
    public static class FaithfulnessJudge
    {
        public static readonly string[] Kinds = { "entity", "item", "outcome", "injury", "contradiction" };

        public const string SystemPrompt =
            "You audit one scene written by the storyteller of a tabletop roleplaying game. The game engine already resolved the turn and " +
            "gave the storyteller a brief. The storyteller may add sensory texture (weather, light, sound, smell), body language, and dialogue " +
            "that fits what the brief says about the people present. It may not add facts that neither the brief nor the earlier scenes support.\n\n" +
            "List every claim in the scene that goes beyond them, using these kinds:\n" +
            "- entity: a person, creature, group or named place that is in neither the brief nor the earlier scenes;\n" +
            "- item: an object the player could take, use or value (loot, a weapon, a key, a clue, coins) that the brief doesn't put there;\n" +
            "- outcome: a result the brief doesn't support (an action succeeding or failing, a discovery, someone arriving, leaving, deciding or revealing something);\n" +
            "- injury: a wound, damage or condition that no roll in the brief produced;\n" +
            "- contradiction: anything that contradicts the brief or the earlier scenes.\n\n" +
            "Do not list texture, mood, body language, or dialogue that only restates or colors what the brief says. When unsure, leave it out.\n" +
            "Reply with JSON only, no prose and no code fence: {\"inventions\":[{\"kind\":\"item\",\"claim\":\"a dagger lies on the table\",\"quote\":\"a dagger glints on the table\"}]}. " +
            "An empty list is a fine answer.";

        public static JsonValue Messages(NarrationInput input, string scene)
        {
            var user = new StringBuilder();
            user.Append("BRIEF (the player's line and what the engine resolved):\n").Append(input.Brief.Trim()).Append("\n\n");
            user.Append("EARLIER SCENES (established before this turn):\n").Append(input.Earlier.Length > 0 ? input.Earlier : "(none)").Append("\n\n");
            if (input.PcCard.Trim().Length > 0) { user.Append("THE PLAYER'S CHARACTER:\n").Append(input.PcCard.Trim()).Append("\n\n"); }
            user.Append("SCENE TO AUDIT:\n").Append((scene ?? string.Empty).Trim());
            var messages = JsonValue.NewArray();
            messages.ArrayValue.Add(Message("system", SystemPrompt));
            messages.ArrayValue.Add(Message("user", user.ToString()));
            return messages;
        }

        /// <summary>Reads the judge's reply; tolerates a code fence or a sentence around the JSON. False (with why) when no list can be read.</summary>
        public static bool TryParse(string reply, out List<JudgedInvention> found, out string error)
        {
            found = new List<JudgedInvention>();
            error = string.Empty;
            string text = reply ?? string.Empty;
            int open = text.IndexOf('{');
            int close = text.LastIndexOf('}');
            JsonValue json;
            if (open < 0 || close <= open || !JsonValue.TryParse(text.Substring(open, close - open + 1), out json) || json.Kind != JsonKind.Object)
            {
                error = "the judge's reply was not JSON";
                return false;
            }
            if (json.Get("inventions").Kind != JsonKind.Array)
            {
                error = "the judge's reply had no inventions list";
                return false;
            }
            foreach (JsonValue item in json.GetArray("inventions"))
            {
                if (item.Kind != JsonKind.Object) { continue; }
                string kind = item.GetString("kind", "other").Trim().ToLowerInvariant();
                if (Array.IndexOf(Kinds, kind) < 0) { kind = "other"; }
                string claim = item.GetString("claim", string.Empty).Trim();
                string quote = item.GetString("quote", string.Empty).Trim();
                if (claim.Length == 0 && quote.Length == 0) { continue; }
                found.Add(new JudgedInvention { Kind = kind, Claim = claim.Length > 0 ? claim : quote, Quote = quote });
            }
            return true;
        }

        private static JsonValue Message(string role, string content)
        {
            var m = JsonValue.NewObject();
            m.ObjectValue["role"] = JsonValue.FromString(role);
            m.ObjectValue["content"] = JsonValue.FromString(content);
            return m;
        }
    }
}
#endif
