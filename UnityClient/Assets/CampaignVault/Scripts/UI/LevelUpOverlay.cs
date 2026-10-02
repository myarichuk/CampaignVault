using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The level-up menu over the sheet: layout in Templates/Sheet/LevelUp.uxml, state in <see cref="LevelUpViewModel"/>.
    /// The page's code-behind only starts the load; it closes itself once the level is gained.
    /// </summary>
    public sealed class LevelUpOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private string _id = string.Empty;

        public LevelUpOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "Level up"; } }
        protected override string TitleIcon { get { return "spark"; } }
        protected override string Template { get { return "Sheet/LevelUp"; } }

        public void SetCharacter(string id) { _id = id ?? string.Empty; }

        protected override ViewModel CreateViewModel() { return new LevelUpViewModel(_state, _controller, Close); }

        public override void OnOpen() { _controller.Run(_controller.LevelUpOpen(_id)); }
    }
}
