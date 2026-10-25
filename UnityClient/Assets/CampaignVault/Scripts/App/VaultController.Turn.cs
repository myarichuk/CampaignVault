using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>The player's turn: sending a line, retry and undo, saving the turn to the chronicle, earlier pages, the transcript export.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // Chat
        // =====================================================================

        /// <summary>Starts a DM turn. False (with a toast) when it can't: no provider, or a turn is running.</summary>
        private string _lastSentLine = string.Empty;
        private string _lastSentShown;
        private TranscriptSegment _lastSentSegment;

        /// <summary>
        /// The last line failed before anything was committed (the provider was busy, a read failed): it can be sent
        /// again as it was. A turn that already wrote to the world is never resent: the engine would apply it twice (A3).
        /// </summary>
        public bool CanRetryLastTurn
        {
            get
            {
                return _lastSentLine.Length > 0 && _s.Driver != null && !_s.Driver.IsBusy && !string.IsNullOrEmpty(_s.Driver.LastError)
                    && !CommittedTheWorld(_s.Driver.CurrentTurn);
            }
        }

        /// <summary>What the player last sent, for the box to take back (empty until something was sent).</summary>
        public string LastSentLine { get { return _lastSentLine; } }

        /// <summary>
        /// The last turn can be taken back: it is still on screen, the DM is idle, and nothing it did reached the
        /// world (a read-only turn, a failed one, a scene the player would rather replay). A committed turn is
        /// never undone here: the world moved.
        /// </summary>
        public bool CanUndoLastTurn
        {
            get
            {
                if (_lastSentSegment == null || _turnActive || _s.Driver == null || _s.Driver.IsBusy || _s.Driver.CurrentTurn == null) { return false; }
                if (CommittedTheWorld(_s.Driver.CurrentTurn)) { return false; }
                int at = IndexOfLastSent();
                if (at < 0) { return false; }
                var segments = _s.Transcript.Segments;
                for (int i = at + 1; i < segments.Count; i++) { if (segments[i].Kind == SegmentKind.Recap) { return false; } }
                return true;
            }
        }

        /// <summary>
        /// Takes the last turn back: its bubbles leave the log and the chronicle file, the DM forgets it was ever
        /// sent. Returns the line as it was typed, for the box, or null when nothing could be undone.
        /// </summary>
        public string UndoLastTurn()
        {
            if (!CanUndoLastTurn) { return null; }
            int at = IndexOfLastSent();
            var segments = _s.Transcript.Segments;
            var gone = new List<TranscriptSegment>();
            for (int i = at; i < segments.Count; i++) { gone.Add(segments[i]); }
            foreach (var seg in gone) { _s.Transcript.Remove(seg); }
            bool wasSaved = string.IsNullOrEmpty(_s.Driver.LastError);
            _s.Driver.DropLastTurn();
            if (wasSaved && _s.Store != null && _s.HasCampaign) { _s.Store.DropLastTurn(_s.CampaignSlug); }
            string line = _lastSentLine;
            _lastSentLine = string.Empty;
            _lastSentShown = null;
            _lastSentSegment = null;
            _s.Notify(StateArea.Driver);
            return line;
        }

        private int IndexOfLastSent()
        {
            var segments = _s.Transcript.Segments;
            for (int i = segments.Count - 1; i >= 0; i--) { if (ReferenceEquals(segments[i], _lastSentSegment)) { return i; } }
            return -1;
        }

        /// <summary>
        /// Sends the failed line again exactly as it was (a setup brief included: typing "retry" would give the DM nothing
        /// to retry). The failed bubble and its warning leave the log, so the story shows one attempt.
        /// </summary>
        public bool RetryLastTurn()
        {
            if (!CanRetryLastTurn) { return false; }
            var segments = _s.Transcript.Segments;
            int at = -1;
            for (int i = segments.Count - 1; i >= 0; i--) { if (segments[i] == _lastSentSegment) { at = i; break; } }
            if (at >= 0)
            {
                var failed = new List<TranscriptSegment>();
                for (int i = at; i < segments.Count; i++) { failed.Add(segments[i]); }
                foreach (var seg in failed) { _s.Transcript.Remove(seg); }
            }
            string line = _lastSentLine, shown = _lastSentShown;
            Run(SendPlayerTextRoutine(line, shown));
            return true;
        }

        public bool SendPlayerText(string text)
        {
            if (!CanSend(text)) { return false; }
            Run(SendPlayerTextRoutine(text));
            return true;
        }

        /// <summary>
        /// One player line, start to finish: the first in-character line of a
        /// sitting opens (or resumes) the session so its recap lands first;
        /// then the DM's turn; then the turn is saved to the chronicle and, if
        /// it changed the world, the table (HP, quests, time) is re-read.
        /// </summary>
        public IEnumerator SendPlayerTextRoutine(string text) { return SendPlayerTextRoutine(text, null); }

        /// <param name="shown">What the player's bubble says, when it differs from what the DM is sent.</param>
        private IEnumerator SendPlayerTextRoutine(string text, string shown)
        {
            if (!CanSend(text)) { yield break; }
            string line = text.Trim();
            string slug = _s.CampaignSlug;
            _turnActive = false;
            if (_s.Session == null && !_s.SetupPending && !Storyteller.IsOocPlayer(line))
            {
                yield return OpenSession(null, "session", false, true);
                if (_s.Driver.IsBusy || _s.CampaignSlug != slug) { yield break; }
            }
            // Before the party exists there's no scene to play: everything is setup talk.
            bool setup = _s.SetupPending && _s.Session == null;
            string sent = setup && !Storyteller.IsOocPlayer(line) ? Storyteller.OocPrefix + " " + line : line;
            var player = new TranscriptSegment { Kind = SegmentKind.Player, Text = shown ?? line };
            _lastSentLine = text;
            _lastSentShown = shown;
            _lastSentSegment = player;
            _turnActive = true;
            try
            {
                _s.Transcript.Add(player);
                _s.Notify(StateArea.Driver);
                yield return _s.Driver.SendPlayerText(sent, _s.Transcript, delegate { _s.Notify(StateArea.Driver); });
            }
            finally
            {
                _turnActive = false;
                // A failed attempt that can be retried is not chronicle yet: the retry replaces it on screen, and the
                // file would otherwise keep both (A21). A failure after a commit stays: the world did change.
                if (!CanRetryLastTurn) { SaveTurn(slug, player); }
            }
            if (setup && _s.CampaignSlug == slug && ChangedTheWorld(_s.Driver.CurrentTurn))
            {
                yield return BeginFirstScene(slug);
                yield break;
            }
            // The table follows the DM's own take_turn results (OnDriverTool); no start_session per turn (A1).
        }

        /// <summary>
        /// The setup turn wrote to the world: if the party now exists, open session 1
        /// and ask for its opening scene (setup turns are out of character, so the
        /// storyteller never narrates them).
        /// </summary>
        private IEnumerator BeginFirstScene(string slug)
        {
            yield return OpenSession(null, "session", false, true, true);
            if (_s.Session == null || _s.CampaignSlug != slug || _s.Driver.IsBusy) { yield break; }
            _s.RaiseToast("The party is ready. Session " + _s.Session.SessionNumber + " begins.", ToastKind.Success);
            yield return SendPlayerTextRoutine(OpeningSceneLine, "▸ The first session begins.");
        }

        internal const string OpeningSceneLine =
            "The first session begins. Set the opening scene where the party stands: get_entity the party's location and whoever is there, "
            + "commit nothing that hasn't happened yet, then DONE.";

        private void SaveTurn(string slug, TranscriptSegment player)
        {
            if (_s.Store == null || string.IsNullOrEmpty(slug) || slug != _s.CampaignSlug) { return; }
            var segments = _s.Transcript.Segments;
            int start = -1;
            for (int i = segments.Count - 1; i >= 0; i--) { if (ReferenceEquals(segments[i], player)) { start = i; break; } }
            if (start < 0) { return; }
            var turn = new List<TranscriptSegment>();
            for (int i = start; i < segments.Count; i++) { turn.Add(segments[i]); }
            AppendChronicle(slug, turn);
        }

        /// <summary>Lines written to the chronicle this run (after the restored page), so "earlier" knows where the file's unseen part ends.</summary>
        private int _appendedLines;
        /// <summary>Lines of the chronicle file already shown, counted from its end.</summary>
        private int _historySkip;

        private void AppendChronicle(string slug, IList<TranscriptSegment> segments)
        {
            if (_s.Store == null) { return; }
            int lines = 0;
            foreach (var seg in segments) { if (TranscriptStore.Persists(seg)) { lines++; } }
            if (_s.Store.Append(slug, segments)) { _appendedLines += lines; }
        }

        /// <summary>The chronicle file holds lines older than the log's oldest.</summary>
        public bool CanLoadEarlier
        {
            get { return _s.Store != null && _s.HasCampaign && _s.Store.LineCount(_s.CampaignSlug) > _historySkip + _appendedLines; }
        }

        /// <summary>The chronicle's previous page goes in above what is shown; its scenes don't reach the DM (they are older than its memory).</summary>
        public bool LoadEarlierHistory()
        {
            if (!CanLoadEarlier) { return false; }
            var page = _s.Store.Load(_s.CampaignSlug, TranscriptStore.LoadLimit, _historySkip + _appendedLines);
            _historySkip += TranscriptStore.LoadLimit;
            if (page.Count > 0) { _s.Transcript.Prepend(page); }
            _s.Notify(StateArea.Driver);
            return page.Count > 0;
        }

        /// <summary>A world-changing tool was called (whether or not it succeeded).</summary>
        internal static bool ChangedTheWorld(TurnRecord turn)
        {
            if (turn == null) { return false; }
            foreach (string tool in turn.Tools) { if (!ReadOnlyTools.Contains(tool)) { return true; } }
            return false;
        }

        /// <summary>A world-changing tool returned success: resending the turn would commit it twice.</summary>
        internal static bool CommittedTheWorld(TurnRecord turn)
        {
            if (turn == null) { return false; }
            foreach (string tool in turn.Succeeded) { if (!ReadOnlyTools.Contains(tool)) { return true; } }
            return false;
        }

        private bool CanSend(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("Chat needs a working AI provider: " + notReady, ToastKind.Warning);
                _s.RequestSetup();
                return false;
            }
            if (!_s.HasCampaign)
            {
                _s.RaiseToast("Choose a campaign first: the Dungeon Master needs a table to run.", ToastKind.Info);
                _s.RequestCampaigns();
                return false;
            }
            if (_s.IsBusy("session"))
            {
                _s.RaiseToast("The session is still opening.", ToastKind.Info);
                return false;
            }
            if (_s.Driver.IsBusy)
            {
                _s.RaiseToast("The DM is still resolving the last action.", ToastKind.Info);
                return false;
            }
            return true;
        }

        public void CancelTurn()
        {
            _s.Driver.Cancel();
            _s.Notify(StateArea.Driver);
        }

        /// <summary>The turn ledger as markdown (see TranscriptExport), for measurement.</summary>
        public string TranscriptMarkdown()
        {
            return TranscriptExport.ToMarkdown(_s.CampaignSlug, _s.Ruleset, _s.Driver.Turns, _s.Driver.SessionUsage);
        }

        /// <summary>Writes the transcript under dir (default: persistentDataPath/Exports); returns the file path, or null.</summary>
        public string ExportTranscript(string dir = null)
        {
            try
            {
                dir = dir ?? System.IO.Path.Combine(Application.persistentDataPath, "Exports");
                System.IO.Directory.CreateDirectory(dir);
                string slug = string.IsNullOrEmpty(_s.CampaignSlug) ? "no-campaign" : _s.CampaignSlug;
                string path = System.IO.Path.Combine(dir, "transcript-" + slug + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md");
                System.IO.File.WriteAllText(path, TranscriptMarkdown());
                return path;
            }
            catch (Exception ex)
            {
                _s.RaiseToast("Export failed: " + ex.Message, ToastKind.Error);
                return null;
            }
        }
    }
}
