using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Table
{
    /// <summary>A quick action under the box: it acts at once, or starts the sentence for the player to finish.</summary>
    public sealed class QuickActionViewModel : ViewModel
    {
        public QuickActionViewModel(string label, string icon, string tooltip, Action run)
        {
            Label = label;
            Icon = icon;
            Tooltip = tooltip;
            Run = run;
            Name = "quick-" + label.ToLowerInvariant().Replace(' ', '-');
        }

        [CreateProperty] public string Name { get; private set; }
        [CreateProperty] public string Label { get; private set; }
        [CreateProperty] public string Icon { get; private set; }
        [CreateProperty] public string Tooltip { get; private set; }
        [CreateProperty] public Action Run { get; private set; }
    }

    /// <summary>
    /// Where the player acts (Templates/Table/CommandBar.uxml): the box and its quick actions, the gate when the
    /// Dungeon Master isn't set up, and the one button that is ACT while the table is idle and STOP while the DM
    /// resolves. The history of sent lines (Up recalls) lives here; the keys that drive it are the view's.
    /// </summary>
    public sealed class CommandBarViewModel : ViewModel
    {
        public const int HistoryLimit = 50;

        private sealed class Quick
        {
            public readonly string Label;
            public readonly string Icon;
            public readonly string Text;
            /// <summary>True: act at once. False: put the words in the box to finish.</summary>
            public readonly bool Immediate;
            public readonly string Tooltip;

            public Quick(string label, string icon, string text, bool immediate, string tooltip)
            {
                Label = label; Icon = icon; Text = text; Immediate = immediate; Tooltip = tooltip;
            }
        }

        private static readonly Quick[] QuickActions =
        {
            new Quick("LOOK", "look", "I take a careful look around.", true, "Look around (acts now)"),
            new Quick("SEARCH", "search", "I search the area thoroughly.", true, "Search the area (acts now)"),
            new Quick("TALK", "talk", "I say, “", false, "Start a line of dialogue"),
            new Quick("ATTACK", "attack", "I attack ", false, "Start an attack: name the target"),
            new Quick("REST", "rest", "We take a short rest.", false, "Propose a rest (edit, then Enter)"),
            new Quick("OUT OF CHARACTER", "ooc", "OOC: ", false, "Talk to the DM out of character"),
        };

        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly List<string> _history = new List<string>();
        private int _recall = -1;
        private string _recalled = string.Empty;
        private string _draft = string.Empty;
        private string _placeholder = string.Empty;
        private bool _gateVisible;
        private string _gateText = string.Empty;
        private bool _ready = true;
        private bool _canAct = true;
        private bool _working;
        private string _actLabel = "ACT";
        private string _actIcon = "send";
        private bool _spinning;
        private string _thinkingText = string.Empty;
        private int _focusRequest;

        public CommandBarViewModel(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            var quick = new List<QuickActionViewModel>();
            foreach (var q in QuickActions)
            {
                var captured = q;
                quick.Add(new QuickActionViewModel(q.Label, q.Icon, q.Tooltip, delegate { Use(captured); }));
            }
            Quicks = quick;
            SetUp = delegate { state.RequestSetup(); };
            Act = ActOrStop;
            Watch(state, StateArea.Driver | StateArea.Busy | StateArea.Campaign | StateArea.Session | StateArea.Providers | StateArea.Preferences);
        }

        [CreateProperty] public List<QuickActionViewModel> Quicks { get; private set; }
        /// <summary>What is in the box. The view writes it as the player types; a quick action or a recall sets it.</summary>
        [CreateProperty] public string Draft { get { return _draft; } set { Set(ref _draft, value ?? string.Empty); } }
        [CreateProperty] public string Placeholder { get { return _placeholder; } private set { Set(ref _placeholder, value); } }
        [CreateProperty] public bool GateVisible { get { return _gateVisible; } private set { Set(ref _gateVisible, value); } }
        [CreateProperty] public string GateText { get { return _gateText; } private set { Set(ref _gateText, value); } }
        /// <summary>A working provider: without one nothing here can do anything.</summary>
        [CreateProperty] public bool Ready { get { return _ready; } private set { Set(ref _ready, value); } }
        /// <summary>STOP must stay reachable mid-turn even if the provider just failed.</summary>
        [CreateProperty] public bool CanAct { get { return _canAct; } private set { Set(ref _canAct, value); } }
        /// <summary>A turn is running, or the session is opening ahead of one.</summary>
        [CreateProperty] public bool Working { get { return _working; } private set { Set(ref _working, value); } }
        [CreateProperty] public string ActLabel { get { return _actLabel; } private set { Set(ref _actLabel, value); } }
        [CreateProperty] public string ActIcon { get { return _actIcon; } private set { Set(ref _actIcon, value); } }
        [CreateProperty] public bool Spinning { get { return _spinning; } private set { Set(ref _spinning, value); } }
        [CreateProperty] public string ThinkingText { get { return _thinkingText; } private set { Set(ref _thinkingText, value); } }
        /// <summary>Goes up to give the box the keyboard.</summary>
        [CreateProperty] public int FocusRequest { get { return _focusRequest; } private set { Set(ref _focusRequest, value); } }
        [CreateProperty] public Action SetUp { get; private set; }
        [CreateProperty] public Action Act { get; private set; }

        public override void Refresh()
        {
            string reason;
            bool ready = _s.ProviderReady(out reason);
            Ready = ready;
            GateVisible = !ready;
            if (!ready) { GateText = "The Dungeon Master isn't set up: " + reason; }
            Placeholder = !ready ? "Set up the Dungeon Master's AI provider to play."
                : !_s.HasCampaign ? "Choose a campaign to begin (the campaign book, top right)…"
                : _s.SetupPending && _s.Session == null ? "Tell the DM about your characters, or answer their questions…"
                : _s.Session == null ? "What do you do? Your first line opens the session."
                : "What do you do?";
            Tick();
        }

        /// <summary>The driver's status text changes between state notifications, so the view polls this while a turn runs.</summary>
        public void Tick()
        {
            bool working = DriverBusy || _s.IsBusy("session");
            Working = working;
            CanAct = Ready || working;
            ActLabel = working ? "STOP" : "ACT";
            ActIcon = working ? "stop" : "send";
            Spinning = working && _s.FxEnabled;
            if (!working) { return; }
            string status = DriverBusy ? _s.Driver.Status : "the table is being set: opening the session…";
            ThinkingText = DisplayText.Plain(string.IsNullOrEmpty(status) ? "the DM is thinking…" : status);
        }

        private bool DriverBusy { get { return _s.Driver != null && _s.Driver.IsBusy; } }

        public void RequestFocus() { FocusRequest = FocusRequest + 1; }

        /// <summary>Puts words in the box and gives it the keyboard (a quick action, or another panel's "talk to X").</summary>
        public void Prefill(string text)
        {
            Draft = text;
            RequestFocus();
        }

        private void Use(Quick q)
        {
            if (q.Immediate && Draft.Trim().Length == 0) { Submit(q.Text); return; }
            string current = Draft.TrimEnd();
            Prefill(current.Length > 0 ? current + " " + q.Text : q.Text);
        }

        private void ActOrStop()
        {
            if (DriverBusy) { _c.CancelTurn(); return; }
            Submit(Draft);
        }

        /// <summary>Enter in the box: acts, or says why not while the DM is still on the last line.</summary>
        public bool Enter(string text)
        {
            if (DriverBusy)
            {
                _s.RaiseToast("The DM is still resolving the last action.", ToastKind.Info);
                return false;
            }
            return Submit(text);
        }

        /// <summary>Sends a line, remembers it, empties the box and keeps the keyboard in it. False: nothing was sent.</summary>
        public bool Submit(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) { return false; }
            if (!_c.SendPlayerText(text)) { return false; }
            Remember(text);
            Draft = string.Empty;
            RequestFocus();
            return true;
        }

        /// <summary>Keeps a sent line for Up to recall (not twice in a row, and the last 50).</summary>
        public void Remember(string text)
        {
            if (_history.Count == 0 || _history[_history.Count - 1] != text) { _history.Add(text); }
            while (_history.Count > HistoryLimit) { _history.RemoveAt(0); }
            _recall = -1;
        }

        /// <summary>Up: the previous line sent (the first time, remembering what was being typed). Null when there is none.</summary>
        public string RecallOlder(string current)
        {
            if (_history.Count == 0) { return null; }
            if (_recall < 0) { _recalled = current ?? string.Empty; _recall = _history.Count; }
            _recall = Math.Max(0, _recall - 1);
            Draft = _history[_recall];
            return Draft;
        }

        /// <summary>Down: the next line, and after the newest the line that was being typed. Null when not recalling.</summary>
        public string RecallNewer()
        {
            if (_recall < 0) { return null; }
            _recall++;
            if (_recall >= _history.Count) { _recall = -1; Draft = _recalled; }
            else { Draft = _history[_recall]; }
            return Draft;
        }
    }
}
