using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Sheet;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// A character, any party id: layout in Templates/Sheet/SheetPage.uxml, state in <see cref="SheetPageViewModel"/>.
    /// The page's code-behind only loads the sheet and sizes and titles the modal for it.
    /// </summary>
    public sealed class CharacterSheetOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private string _id = string.Empty;

        public CharacterSheetOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "Character"; } }
        protected override string TitleIcon { get { return "character"; } }
        protected override string ModalClass { get { return "cv-modal--sheet"; } }
        protected override string Template { get { return "Sheet/SheetPage"; } }

        private SheetPageViewModel Page { get { return ViewModel as SheetPageViewModel; } }

        /// <summary>True once the sheet for the current id has loaded (or failed).</summary>
        public bool Loaded { get { return Page == null || Page.Loaded; } }

        public void SetCharacter(string id) { _id = id ?? string.Empty; }

        protected override ViewModel CreateViewModel() { return new SheetPageViewModel(_state, _controller, _id); }

        public override void OnOpen()
        {
            var page = Page;
            Fit(page);
            // Sheet or stat block decides the modal's width and title (and changes on "play as this character").
            page.propertyChanged += delegate (object sender, UnityEngine.UIElements.BindablePropertyChangedEventArgs e)
            {
                if (e.propertyName == "Content") { Fit(page); }
            };
            _controller.Run(page.Load());
        }

        private void Fit(SheetPageViewModel page)
        {
            SetModalClass("cv-modal--statblock", page.IsStatBlock);
            SetTitle(page.Title);
        }
    }
}
