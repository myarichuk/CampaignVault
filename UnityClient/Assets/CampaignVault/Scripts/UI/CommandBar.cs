using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Where the player acts: a multi-line field (Enter acts, Shift+Enter
    /// breaks the line, Up recalls earlier lines), quick actions that either
    /// act at once or start the sentence, and one button that is ACT while
    /// the table is idle and STOP while the DM resolves. Esc never discards
    /// what was typed.
    /// </summary>
    public sealed class CommandBar
    {
        private const int HistoryLimit = 50;

        private sealed class QuickAction
        {
            public readonly string Label;
            public readonly string Icon;
            public readonly string Text;
            /// <summary>True: act at once. False: put the words in the box to finish.</summary>
            public readonly bool Immediate;
            public readonly string Tooltip;

            public QuickAction(string label, string icon, string text, bool immediate, string tooltip)
            {
                Label = label; Icon = icon; Text = text; Immediate = immediate; Tooltip = tooltip;
            }
        }

        private static readonly QuickAction[] Quick =
        {
            new QuickAction("LOOK", "look", "I take a careful look around.", true, "Look around (acts now)"),
            new QuickAction("SEARCH", "search", "I search the area thoroughly.", true, "Search the area (acts now)"),
            new QuickAction("TALK", "talk", "I say, “", false, "Start a line of dialogue"),
            new QuickAction("ATTACK", "attack", "I attack ", false, "Start an attack: name the target"),
            new QuickAction("REST", "rest", "We take a short rest.", false, "Propose a rest (edit, then Enter)"),
            new QuickAction("OUT OF CHARACTER", "ooc", "OOC: ", false, "Talk to the DM out of character"),
        };

        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly TextField _input;
        private readonly Button _act;
        private readonly VisualElement _thinking;
        private readonly Label _thinkingText;
        private readonly VisualElement _thinkingDie;
        private readonly List<string> _history = new List<string>();
        private int _recall = -1;
        private string _draft = string.Empty;
        private bool _busyShown;
        private float _spin;

        public CommandBar(VisualElement column, VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;

            _thinking = Ui.El("cv-thinking");
            _thinkingDie = Ui.Icon("d20");
            _thinking.Add(_thinkingDie);
            _thinkingText = Ui.Text(string.Empty, "cv-thinking__text");
            _thinking.Add(_thinkingText);
            column.Add(_thinking);

            var bar = Ui.Frame(Ui.El("cv-command"));
            var quick = Ui.El("cv-command__quick");
            foreach (var q in Quick)
            {
                var captured = q;
                var b = Ui.Button(q.Label, q.Icon, "cv-btn--small cv-btn--ghost", delegate { UseQuick(captured); });
                TooltipLayer.Attach(b, q.Tooltip);
                quick.Add(b);
            }
            bar.Add(quick);

            var row = Ui.El("cv-command__row");
            _input = Ui.Field("What do you do?", null, true, "cv-command__input");
            _input.name = "CommandInput";
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            row.Add(_input);
            _act = Ui.Button("ACT", "send", "cv-btn--primary cv-command__send", OnActOrStop);
            _act.name = "CommandAct";
            row.Add(_act);
            bar.Add(row);

            var hint = Ui.El("cv-command__hint");
            hint.Add(Ui.Text("ENTER TO ACT", "cv-caption"));
            hint.Add(Ui.Text("SHIFT+ENTER NEW LINE", "cv-caption"));
            hint.Add(Ui.Text("UP TO RECALL", "cv-caption"));
            bar.Add(hint);
            column.Add(bar);

            state.Changed += delegate (StateArea area)
            {
                if ((area & (StateArea.Driver | StateArea.Busy)) != 0) { PaintBusy(); }
                if ((area & (StateArea.Campaign | StateArea.Session)) != 0) { PaintPlaceholder(); }
            };
            // The driver's status text changes between Notify calls: poll it while busy.
            _thinking.schedule.Execute(Tick).Every(33);
            PaintBusy();
            PaintPlaceholder();
        }

        /// <summary>The empty box says what the next line will do: nothing yet, open the session, or play.</summary>
        private void PaintPlaceholder()
        {
            string text = !_state.HasCampaign ? "Choose a campaign to begin (the campaign book, top right)…"
                : _state.Session == null ? "What do you do? Your first line opens the session."
                : "What do you do?";
            _input.textEdition.placeholder = text;
        }

        public TextField Input { get { return _input; } }

        public void Focus() { _input.Focus(); }

        /// <summary>Puts words in the box (for other panels: "use item", "talk to X").</summary>
        public void Prefill(string text)
        {
            _input.value = text;
            _input.Focus();
            _input.SelectRange(text.Length, text.Length);
        }

        private void UseQuick(QuickAction q)
        {
            if (q.Immediate && _input.value.Trim().Length == 0) { Submit(q.Text); return; }
            string current = _input.value.TrimEnd();
            Prefill(current.Length > 0 ? current + " " + q.Text : q.Text);
        }

        private void OnActOrStop()
        {
            if (_state.Driver.IsBusy) { _controller.CancelTurn(); return; }
            Submit(_input.value);
        }

        private void Submit(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) { return; }
            if (!_controller.SendPlayerText(text)) { return; }
            if (_history.Count == 0 || _history[_history.Count - 1] != text) { _history.Add(text); }
            while (_history.Count > HistoryLimit) { _history.RemoveAt(0); }
            _recall = -1;
            _input.value = string.Empty;
            // Keep the keyboard in the box: every line shouldn't cost a click.
            _input.schedule.Execute(() => { _input.Focus(); }).StartingIn(1);
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
            if (enter && !e.shiftKey)
            {
                // Both the keyCode event and its character event must stop, or the field adds a newline.
                e.StopImmediatePropagation();
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (_state.Driver.IsBusy) { _state.RaiseToast("The DM is still resolving the last action.", ToastKind.Info); }
                    else { Submit(_input.value); }
                }
                return;
            }
            if (e.keyCode == KeyCode.UpArrow && _history.Count > 0 && CaretOnFirstLine())
            {
                e.StopImmediatePropagation();
                if (_recall < 0) { _draft = _input.value; _recall = _history.Count; }
                _recall = Mathf.Max(0, _recall - 1);
                ShowRecall();
                return;
            }
            if (e.keyCode == KeyCode.DownArrow && _recall >= 0 && CaretOnLastLine())
            {
                e.StopImmediatePropagation();
                _recall++;
                if (_recall >= _history.Count) { _recall = -1; _input.value = _draft; }
                else { ShowRecall(); }
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                // Esc leaves the box (so global Esc can close a page) but keeps the words.
                _input.Blur();
            }
        }

        private void ShowRecall()
        {
            string text = _history[_recall];
            _input.value = text;
            _input.SelectRange(text.Length, text.Length);
        }

        private bool CaretOnFirstLine()
        {
            string v = _input.value ?? string.Empty;
            int caret = Mathf.Clamp(_input.cursorIndex, 0, v.Length);
            return v.LastIndexOf('\n', Mathf.Max(0, caret - 1)) < 0 || caret == 0;
        }

        private bool CaretOnLastLine()
        {
            string v = _input.value ?? string.Empty;
            int caret = Mathf.Clamp(_input.cursorIndex, 0, v.Length);
            return v.IndexOf('\n', caret) < 0;
        }

        /// <summary>A turn is running, or the session is opening ahead of one.</summary>
        private bool Working
        {
            get { return (_state.Driver != null && _state.Driver.IsBusy) || _state.IsBusy("session"); }
        }

        private void PaintBusy()
        {
            bool busy = Working;
            if (busy == _busyShown) { return; }
            _busyShown = busy;
            Ui.SetButtonText(_act, busy ? "STOP" : "ACT");
            _act.EnableInClassList("cv-btn--primary", !busy);
            _act.EnableInClassList("cv-btn--danger", busy);
            var icon = _act.Q(className: "cv-icon");
            icon.EnableInClassList("cv-icon--send", !busy);
            icon.EnableInClassList("cv-icon--stop", busy);
            _thinking.EnableInClassList("cv-thinking--on", busy);
        }

        private void Tick()
        {
            PaintBusy();
            if (!_busyShown) { return; }
            string status = _state.Driver.IsBusy ? _state.Driver.Status : "the table is being set: opening the session…";
            Ui.SetText(_thinkingText, string.IsNullOrEmpty(status) ? "the DM is thinking…" : status);
            if (_state.FxEnabled)
            {
                _spin = (_spin + 6f) % 360f;
                _thinkingDie.style.rotate = new Rotate(_spin);
            }
        }
    }
}
