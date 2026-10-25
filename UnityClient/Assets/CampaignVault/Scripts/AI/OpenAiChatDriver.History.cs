using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>The model's memory of the conversation: compaction of finished turns, the character budget, what survives a trim, passages for the storyteller, undo.</summary>
    public partial class OpenAiChatDriver
    {
        /// <summary>
        /// Gives the storyteller back its recent scenes (from the client's saved
        /// history) after a restart, so the prose picks up where it left off.
        /// </summary>
        /// <summary>
        /// Undo: the last turn leaves the model's memory as if it had never been sent (its user message and
        /// everything after it, its passage, its record). The caller decides it committed nothing. False while
        /// a turn runs or when there is none.
        /// </summary>
        public bool DropLastTurn()
        {
            if (IsBusy || CurrentTurn == null) { return false; }
            if (_turnStart >= 0 && _turnStart < _history.Count) { _history.RemoveRange(_turnStart, _history.Count - _turnStart); }
            if (_passages.Count > 0 && _passages[_passages.Count - 1].Key == CurrentTurn.Player) { _passages.RemoveAt(_passages.Count - 1); }
            CurrentTurn = null;
            LastError = string.Empty;
            LastFailure = null;
            return true;
        }

        public void RestorePassages(IList<KeyValuePair<string, string>> passages)
        {
            _passages.Clear();
            if (passages == null) { return; }
            foreach (var p in passages)
            {
                _passages.Add(new KeyValuePair<string, string>(p.Key, Storyteller.Cap(p.Value, Storyteller.MaxPassageChars)));
            }
            while (_passages.Count > Storyteller.MaxPassages) { _passages.RemoveAt(0); }
        }


        /// <summary>
        /// Shrinks every message before the new turn: tool results become a
        /// one-line summary, skill bodies a reload hint, and bulky tool-call
        /// arguments an elision marker. Call ids stay intact so the protocol
        /// pairing (assistant tool_calls -> tool results) remains valid.
        /// </summary>
        private void CompactFinishedTurns()
        {
            for (int i = 0; i < _history.Count; i++)
            {
                var message = _history[i];
                string role = message.GetString("role", string.Empty);
                if (role == "user")
                {
                    // Only the newest loop message carries the last scene.
                    string content = message.GetString("content", string.Empty);
                    string stripped = Storyteller.StripScene(content);
                    if (!ReferenceEquals(stripped, content)) { message.ObjectValue["content"] = JsonValue.FromString(stripped); }
                }
                else if (role == "tool")
                {
                    string callId = message.GetString("tool_call_id", string.Empty);
                    string name;
                    _callNames.TryGetValue(callId, out name);
                    message.ObjectValue["content"] = JsonValue.FromString(
                        CompactResult(name ?? string.Empty, message.GetString("content", string.Empty)));
                }
                else if (role == "assistant")
                {
                    // Reasoning replay only matters inside the turn that made it.
                    message.ObjectValue.Remove("reasoning_details");
                    foreach (var call in message.GetArray("tool_calls"))
                    {
                        var fn = call.Get("function");
                        string arguments = fn.GetString("arguments", "{}");
                        if (arguments.Length <= CompactedArgumentChars) { continue; }
                        var marker = JsonValue.NewObject();
                        marker.ObjectValue["elided"] = JsonValue.FromString(arguments.Length + " chars, already sent and applied");
                        fn.ObjectValue["arguments"] = JsonValue.FromString(marker.ToJson());
                    }
                }
            }
            _turnStart = _history.Count;
        }

        internal static string CompactResult(string toolName, string content)
        {
            if (content.StartsWith("[earlier", StringComparison.Ordinal)) { return content; }
            if (toolName == SystemPromptProvider.LoadSkillTool)
            {
                return "[earlier skill load, dropped to save context; call load_skill again if you need it]";
            }
            string summary = content;
            string kept = string.Empty;
            JsonValue parsed;
            if (JsonValue.TryParse(content, out parsed) && parsed.Kind == JsonKind.Object)
            {
                string envelopeSummary = parsed.GetString("summary", string.Empty);
                if (!parsed.GetBool("success", true)) { envelopeSummary = "FAILED: " + envelopeSummary; }
                if (envelopeSummary.Length > 0) { summary = envelopeSummary; }
                kept = KeptAfterCompaction(parsed);
            }
            if (summary.Length > CompactedResultChars) { summary = summary.Substring(0, CompactedResultChars) + "…"; }
            return "[earlier " + toolName + " result] " + summary + kept;
        }

        private const int KeptLineChars = 200;
        private const int KeptLines = 3;

        /// <summary>
        /// What a compacted result must still say (A4): the party fingerprint the model has to echo, the
        /// warnings and ENGINE WARNINGs it has to fix on its next take_turn. Everything else is history.
        /// </summary>
        internal static string KeptAfterCompaction(JsonValue envelope)
        {
            var data = envelope.Get("data");
            if (data.Kind != JsonKind.Object) { data = envelope; }
            var sb = new StringBuilder();
            string fingerprint = data.GetString("partyFingerprint", string.Empty);
            if (fingerprint.Length > 0) { sb.Append(" partyFingerprint=").Append(fingerprint); }
            KeepLines(sb, "warnings", data.GetArray("warnings"));
            var pressure = envelope.GetArray("worldPressure");
            if (pressure.Count == 0) { pressure = data.Get("worldStateDelta").GetArray("worldPressure"); }
            if (pressure.Count == 0) { pressure = data.Get("worldState").GetArray("worldPressure"); }
            KeepLines(sb, "pressure", pressure);
            return sb.ToString();
        }

        private static void KeepLines(StringBuilder sb, string label, List<JsonValue> lines)
        {
            int shown = 0;
            foreach (var line in lines)
            {
                if (line.Kind != JsonKind.String || line.StringValue.Trim().Length == 0) { continue; }
                if (shown++ >= KeptLines) { sb.Append(" (+").Append(lines.Count - KeptLines).Append(" more)"); break; }
                string text = line.StringValue.Trim();
                if (text.Length > KeptLineChars) { text = text.Substring(0, KeptLineChars) + "…"; }
                sb.Append(shown == 1 ? " " + label + ": " : " | ").Append(text);
            }
        }

        /// <summary>
        /// Drops the oldest whole turns (a user message up to the next user
        /// message) while history is over budget, never touching the current
        /// turn, so a tool result is never replayed without the tool_calls
        /// that produced it.
        /// </summary>
        /// <summary>Older turns were dropped since the last turn; the next user message says so, once.</summary>
        private bool _historyTrimmed;

        internal const string TrimmedNote = "(Client note: earlier turns were dropped from your context to fit its budget. Send forceFullReseed:true on this turn's take_turn.)";

        private void TrimToBudget()
        {
            while (HistoryChars() > MaxHistoryChars)
            {
                int nextUser = -1;
                // _turnStart is the current user message: the furthest a cut may reach.
                for (int i = 1; i <= _turnStart && i < _history.Count; i++)
                {
                    if (_history[i].GetString("role", string.Empty) == "user") { nextUser = i; break; }
                }
                if (nextUser < 0) { break; }
                _history.RemoveRange(0, nextUser);
                _turnStart -= nextUser;
                _historyTrimmed = true;
            }
            PruneCallNames();
        }

        /// <summary>
        /// Drops call-id labels for tool calls no longer in history, so the
        /// map can't grow without bound over a long campaign.
        /// </summary>
        private void PruneCallNames()
        {
            if (_callNames.Count == 0) { return; }
            var live = new HashSet<string>();
            foreach (var message in _history)
            {
                if (message.GetString("role", string.Empty) != "assistant") { continue; }
                foreach (var call in message.GetArray("tool_calls"))
                {
                    string id = call.GetString("id", string.Empty);
                    if (!string.IsNullOrEmpty(id)) { live.Add(id); }
                }
            }
            var dead = new List<string>();
            foreach (string id in _callNames.Keys)
            {
                if (!live.Contains(id)) { dead.Add(id); }
            }
            foreach (string id in dead) { _callNames.Remove(id); }
        }

        private int HistoryChars()
        {
            int total = 0;
            foreach (var message in _history) { total += message.ToJson().Length; }
            return total;
        }
    }
}
