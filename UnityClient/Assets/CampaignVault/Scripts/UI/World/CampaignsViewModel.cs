using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.World
{
    /// <summary>One campaign in the book, kept per slug across refreshes. Shown with Templates/World/CampaignCard.uxml.</summary>
    public sealed class CampaignCardViewModel : ViewModel, IKeyed
    {
        private bool _active;
        private bool _armed;
        private bool _deleting;
        private string _display = string.Empty;
        private string _system = string.Empty;

        public CampaignCardViewModel(string slug, Action play, Action close, Action delete)
        {
            Key = slug;
            Slug = slug;
            PlayName = "campaign-play-" + slug;
            DeleteName = "campaign-delete-" + slug;
            Play = play;
            Return = close;
            Delete = delete;
        }

        public string Key { get; private set; }
        [CreateProperty] public string Slug { get; private set; }
        [CreateProperty] public string PlayName { get; private set; }
        [CreateProperty] public string DeleteName { get; private set; }
        [CreateProperty] public Action Play { get; private set; }
        [CreateProperty] public Action Return { get; private set; }
        [CreateProperty] public Action Delete { get; private set; }
        [CreateProperty] public string Display { get { return _display; } private set { Set(ref _display, value); } }
        /// <summary>The rules system as a chip's text; empty when the campaign names none.</summary>
        [CreateProperty] public string System { get { return _system; } private set { Set(ref _system, value); } }
        [CreateProperty] public bool Active { get { return _active; } private set { Set(ref _active, value); } }
        [CreateProperty] public bool Armed { get { return _armed; } private set { Set(ref _armed, value); } }
        [CreateProperty] public bool Deleting { get { return _deleting; } private set { Set(ref _deleting, value); } }
        [CreateProperty] public bool CanDelete { get { return !_deleting; } }
        [CreateProperty] public string DeleteLabel { get { return _deleting ? "DELETING…" : _armed ? "CONFIRM DELETE" : "DELETE"; } }

        public void Update(CampaignRow row, bool active, bool armed, bool deleting)
        {
            Display = DisplayText.Plain(row.Display);
            System = string.IsNullOrEmpty(row.System) ? string.Empty : DisplayText.Plain(CampaignsViewModel.SystemLabel(row.System)).ToUpperInvariant();
            Active = active;
            bool label = _armed != armed || _deleting != deleting;
            Armed = armed;
            Deleting = deleting;
            if (label) { Notify("DeleteLabel"); Notify("CanDelete"); }
        }
    }

    /// <summary>The campaign book (Templates/World/Campaigns.uxml): every campaign on the server as a card. Play, or delete (two taps).</summary>
    public sealed class CampaignsViewModel : ViewModel
    {
        private const long DisarmAfterMs = 4000;

        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly Action _close;
        private readonly Later _later;
        private List<CampaignCardViewModel> _cards = new List<CampaignCardViewModel>();
        private string _notice = string.Empty;
        private string _noticeIcon = "campaigns";
        private string _armed = string.Empty;

        public CampaignsViewModel(VaultAppState state, VaultController controller, Action close, Action openOnboarding, Later later)
        {
            _s = state;
            _c = controller;
            _close = close;
            _later = later;
            NewCampaign = delegate { close(); openOnboarding(); };
            ReloadList = delegate { controller.Run(controller.ListCampaigns()); };
            Watch(state, StateArea.Campaigns | StateArea.Campaign | StateArea.Busy);
        }

        [CreateProperty] public List<CampaignCardViewModel> Cards { get { return _cards; } private set { SetList(ref _cards, value); } }
        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public string NoticeIcon { get { return _noticeIcon; } private set { Set(ref _noticeIcon, value); } }
        [CreateProperty] public Action NewCampaign { get; private set; }
        [CreateProperty] public Action ReloadList { get; private set; }

        public override void Refresh()
        {
            if (!_s.CampaignsLoaded || (_s.IsBusy("campaigns") && _s.Campaigns.Count == 0))
            {
                Empty("campaigns", "Opening the campaign book…");
            }
            else if (_s.CampaignsError.Length > 0)
            {
                Empty("warning", "Could not list campaigns: " + _s.CampaignsError);
            }
            else if (_s.Campaigns.Count == 0)
            {
                Empty("quests", "No campaigns yet. Create one to begin.");
            }
            else
            {
                Notice = string.Empty;
                Cards = ItemList.Sync(_cards, _s.Campaigns, delegate (CampaignRow row) { return row.Slug; },
                    delegate (CampaignRow row) { return NewCard(row); },
                    delegate (CampaignCardViewModel card, CampaignRow row)
                    {
                        card.Update(row, row.Slug == _s.CampaignSlug, row.Slug == _armed, _s.IsBusy("delete:" + row.Slug));
                    });
            }
        }

        private void Empty(string icon, string text)
        {
            NoticeIcon = icon;
            Notice = DisplayText.Plain(text);
            Cards = new List<CampaignCardViewModel>();
        }

        private CampaignCardViewModel NewCard(CampaignRow row)
        {
            string slug = row.Slug;
            string system = row.System;
            return new CampaignCardViewModel(slug,
                delegate
                {
                    _c.SelectCampaign(slug, system);
                    _close();
                    _c.Run(_c.RefreshTable());
                },
                _close,
                delegate { PressDelete(slug); });
        }

        /// <summary>The first tap arms the button; a second tap within a few seconds deletes.</summary>
        private void PressDelete(string slug)
        {
            if (_armed != slug)
            {
                _armed = slug;
                Refresh();
                // Disarm if they walk away.
                _later(delegate { if (_armed == slug) { _armed = string.Empty; Refresh(); } }, DisarmAfterMs);
                return;
            }
            _armed = string.Empty;
            _c.Run(_c.DeleteCampaign(slug));
        }

        public static string SystemLabel(string system)
        {
            for (int i = 0; i < OnboardingState.SystemOptions.Length; i++)
            {
                if (string.Equals(OnboardingState.SystemOptions[i], system, StringComparison.OrdinalIgnoreCase)) { return OnboardingState.SystemLabels[i]; }
            }
            if (string.Equals(system, "dnd5e", StringComparison.OrdinalIgnoreCase)) { return "D&D 5e"; }
            if (string.Equals(system, "pf2e", StringComparison.OrdinalIgnoreCase)) { return "Pathfinder 2e"; }
            return system;
        }
    }
}
