using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.World;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Guided campaign creation: layout in Templates/Onboarding, state in <see cref="OnboardingViewModel"/>. The one
    /// thing it does itself is keep the newest brainstorm message in view.
    /// </summary>
    public sealed class OnboardingOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;

        public OnboardingOverlay(VaultAppState state, VaultController controller)
        {
            _state = state;
            _controller = controller;
        }

        protected override string Title { get { return "A new campaign"; } }
        protected override string TitleIcon { get { return "seal"; } }
        protected override bool Narrow { get { return true; } }
        protected override string Template { get { return "Onboarding/Onboarding"; } }

        protected override ViewModel CreateViewModel()
        {
            // A finished or failed setup starts fresh.
            var phase = _state.Onboarding.Phase;
            if (phase == OnboardingPhase.Done || phase == OnboardingPhase.Failed) { _controller.ResetOnboarding(); }
            var onboarding = new OnboardingViewModel(_state, _controller, Close);
            onboarding.propertyChanged += delegate (object sender, BindablePropertyChangedEventArgs e)
            {
                if (e.propertyName.ToString() == "ScrollTick") { ScrollToNewest(); }
            };
            return onboarding;
        }

        private void ScrollToNewest()
        {
            Body.schedule.Execute(() =>
            {
                var scroll = Body.GetFirstAncestorOfType<ScrollView>();
                if (scroll != null) { scroll.scrollOffset = new Vector2(0, float.MaxValue); }
            }).StartingIn(30);
        }
    }
}
