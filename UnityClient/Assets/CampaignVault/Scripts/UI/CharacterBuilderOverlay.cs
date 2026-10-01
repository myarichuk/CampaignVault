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

        public CharacterBuilderOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return _kind == "companion" ? "A new companion" : "A new character"; } }
        protected override string TitleIcon { get { return "character"; } }
        protected override string ModalClass { get { return "cv-modal--builder"; } }
        protected override string Template { get { return "Builder/CharacterBuilder"; } }

        /// <summary>pc or companion; takes effect on the next open.</summary>
        public void SetKind(string kind) { _kind = string.IsNullOrEmpty(kind) ? "pc" : kind; }

        protected override ViewModel CreateViewModel() { return new BuilderViewModel(_state, _controller); }

        public override void OnOpen()
        {
            _controller.Run(_controller.BeginBuilder(_kind));
        }

        public override void OnClose()
        {
            // What was typed last still reaches the draft's preview next time.
            _state.Builder.PreviewCurrent = false;
        }
    }
}
