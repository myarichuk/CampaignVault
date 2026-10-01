using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Controls;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Table;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Where the player acts: layout in Templates/Table/CommandBar.uxml, state and history in
    /// <see cref="CommandBarViewModel"/>. What is left here is what a binding can't do: the keys of a multi-line box
    /// (Enter acts, Shift+Enter breaks the line, Up and Down recall, Esc leaves the box but keeps the words) and the
    /// poll for the driver's status while it works. Esc never discards what was typed.
    /// </summary>
    public sealed class CommandBar
    {
        private readonly CommandBarViewModel _vm;
        private readonly VaultField _input;

        public CommandBar(VisualElement host, VaultAppState state, VaultController controller)
        {
            _vm = new CommandBarViewModel(state, controller);
            Templates.CloneInto("Table/CommandBar", host);
            _vm.Refresh();
            host.dataSource = _vm;
            _input = host.Q<VaultField>("CommandInput");
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            host.schedule.Execute(_vm.Tick).Every(33);
        }

        public CommandBarViewModel ViewModel { get { return _vm; } }
        public TextField Input { get { return _input; } }

        public void Focus() { _vm.RequestFocus(); }

        /// <summary>Puts words in the box (for other panels: "use item", "talk to X").</summary>
        public void Prefill(string text) { _vm.Prefill(text); }

        private void OnKeyDown(KeyDownEvent e)
        {
            bool enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
            if (enter && !e.shiftKey)
            {
                // Both the keyCode event and its character event must stop, or the field adds a newline.
                e.StopImmediatePropagation();
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    // The binding carries typing to the view model on the panel's next update; this key can come sooner.
                    _vm.Draft = _input.value;
                    if (_vm.Enter(_input.value)) { _input.value = string.Empty; }
                }
                return;
            }
            if (e.keyCode == KeyCode.UpArrow && CaretOnFirstLine())
            {
                string older = _vm.RecallOlder(_input.value);
                if (older == null) { return; }
                e.StopImmediatePropagation();
                Show(older);
                return;
            }
            if (e.keyCode == KeyCode.DownArrow && CaretOnLastLine())
            {
                string newer = _vm.RecallNewer();
                if (newer == null) { return; }
                e.StopImmediatePropagation();
                Show(newer);
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                // Esc leaves the box (so global Esc can close a page) but keeps the words.
                _input.Blur();
            }
        }

        private void Show(string text)
        {
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
    }
}
