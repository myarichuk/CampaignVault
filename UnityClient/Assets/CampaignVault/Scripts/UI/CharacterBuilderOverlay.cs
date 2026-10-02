using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Builder;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The character builder: layout in Templates/Builder/CharacterBuilder.uxml, state in
    /// <see cref="BuilderViewModel"/>. Binding updates only what changed, so typing, focus and hover survive every
    /// reply from the server.
    /// </summary>
    public sealed class CharacterBuilderOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private string _kind = "pc";
        private bool _forParty;
        private string _editId = string.Empty;

        public CharacterBuilderOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return _kind == "companion" ? "A new companion" : _editId.Length > 0 ? "Edit character" : "A new character"; } }
        protected override string TitleIcon { get { return "character"; } }
        protected override string ModalClass { get { return "cv-modal--builder"; } }
        protected override string Template { get { return "Builder/CharacterBuilder"; } }

        /// <summary>pc or companion; takes effect on the next open.</summary>
        public void SetKind(string kind)
        {
            _kind = string.IsNullOrEmpty(kind) ? "pc" : kind;
            _forParty = false;
            _editId = string.Empty;
        }

        /// <summary>
        /// For the campaign being set up in onboarding, not the one at the table: a new party member (empty id) or the
        /// built character with this id.
        /// </summary>
        public void SetPartyTarget(string editId, string kind)
        {
            _kind = string.IsNullOrEmpty(kind) ? "pc" : kind;
            _forParty = true;
            _editId = editId ?? string.Empty;
        }

        protected override ViewModel CreateViewModel() { return new BuilderViewModel(_state, _controller); }

        public override void OnOpen()
        {
            _controller.Run(_controller.BeginBuilder(_kind, _forParty, _editId));
        }

        public override void OnClose()
        {
            // What was typed last still reaches the draft's preview next time.
            _state.Builder.PreviewCurrent = false;
        }
    }
}
