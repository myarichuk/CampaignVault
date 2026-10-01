using System;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Dialogs;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>A yes/no question that doesn't block the thread (no native dialogs). Layout: Templates/Dialogs/Confirm.uxml.</summary>
    public sealed class ConfirmOverlay : Overlay
    {
        private readonly string _title;
        private readonly string _message;
        private readonly string _confirm;
        private readonly bool _danger;
        private readonly Action _onConfirm;

        public ConfirmOverlay(string title, string message, string confirm, bool danger, Action onConfirm)
        {
            _title = title;
            _message = message;
            _confirm = confirm;
            _danger = danger;
            _onConfirm = onConfirm;
        }

        protected override string Title { get { return _title; } }
        protected override bool Narrow { get { return true; } }
        protected override string Template { get { return "Dialogs/Confirm"; } }

        protected override ViewModel CreateViewModel()
        {
            return new ConfirmViewModel(_message, _confirm, _danger, Close, delegate { Close(); _onConfirm(); });
        }
    }

    /// <summary>F12: what the driver last sent and got back, token usage, and its tool log. Everything is selectable.</summary>
    public sealed class InspectorOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;

        public InspectorOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "Inspector"; } }
        protected override string TitleIcon { get { return "inspect"; } }
        protected override string Template { get { return "Dialogs/Inspector"; } }

        protected override ViewModel CreateViewModel() { return new InspectorViewModel(_state, _controller); }
    }
}
