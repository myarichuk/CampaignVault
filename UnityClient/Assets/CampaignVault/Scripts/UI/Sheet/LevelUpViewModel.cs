using System;
using System.Collections.Generic;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.UI.Builder;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Sheet
{
    /// <summary>
    /// The level-up menu (Templates/Sheet/LevelUp.uxml): what the next level gives, and its choices as the builder's own
    /// sections and cards (one section per slot, "Level 4 · Ability Score Improvement"). The server decides what is
    /// offered and whether the picks are allowed; this page collects them and sends them.
    /// </summary>
    public sealed class LevelUpViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly Action _close;
        private string _notice = string.Empty;
        private bool _noticeIsError;
        private string _intro = string.Empty;
        private string _xpLine = string.Empty;
        private string _gains = string.Empty;
        private string _error = string.Empty;
        private string _status = string.Empty;
        private bool _canApply;
        private string _applyLabel = "LEVEL UP";
        private List<SpellSectionViewModel> _sections = new List<SpellSectionViewModel>();
        private bool _closed;

        public LevelUpViewModel(VaultAppState state, VaultController controller, Action close)
        {
            _s = state;
            _c = controller;
            _close = close;
            Apply = delegate { _c.Run(_c.LevelUpApply()); };
            Watch(state, StateArea.LevelUp);
        }

        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public bool NoticeIsError { get { return _noticeIsError; } private set { Set(ref _noticeIsError, value); } }
        /// <summary>"Hild · Fighter · level 3 → 4".</summary>
        [CreateProperty] public string Intro { get { return _intro; } private set { Set(ref _intro, value); } }
        [CreateProperty] public string XpLine { get { return _xpLine; } private set { Set(ref _xpLine, value); } }
        /// <summary>What the class gives at the new level, one feature per line.</summary>
        [CreateProperty] public string Gains { get { return _gains; } private set { Set(ref _gains, value); } }
        [CreateProperty] public string Error { get { return _error; } private set { Set(ref _error, value); } }
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public bool CanApply { get { return _canApply; } private set { Set(ref _canApply, value); } }
        [CreateProperty] public string ApplyLabel { get { return _applyLabel; } private set { Set(ref _applyLabel, value); } }
        [CreateProperty] public List<SpellSectionViewModel> Sections { get { return _sections; } private set { SetList(ref _sections, value); } }
        [CreateProperty] public Action Apply { get; private set; }

        public override void Refresh()
        {
            var l = _s.LevelUp;
            if (l.Done && !_closed) { _closed = true; _close(); return; }
            var offer = l.Offer;
            Error = DisplayText.Plain(l.Error);
            if (offer == null)
            {
                Notice = l.Loading ? "Reading what the next level offers…" : l.Error.Length > 0 ? "Couldn't read the level." : string.Empty;
                NoticeIsError = !l.Loading && l.Error.Length > 0;
                Intro = string.Empty;
                XpLine = string.Empty;
                Gains = string.Empty;
                Sections = new List<SpellSectionViewModel>();
                CanApply = false;
                Status = string.Empty;
                return;
            }
            Notice = offer.Slots.Count == 0 && offer.Features.Count == 0 ? "Nothing to choose: the level just comes." : string.Empty;
            NoticeIsError = false;
            var status = offer.Status;
            var parts = new List<string> { offer.Name };
            if (offer.ClassName.Length > 0) { parts.Add(offer.ClassName); }
            if (status != null) { parts.Add("level " + status.Level + " → " + status.TargetLevel); }
            Intro = DisplayText.Plain(string.Join(" · ", parts.ToArray()));
            XpLine = status != null ? DisplayText.Plain(status.XpLine) : string.Empty;
            Gains = DisplayText.Plain(string.Join("\n", offer.Features.ToArray()));

            var picks = LevelChoices.Picks(l.Choice);
            Sections = ItemList.Sync(_sections, offer.Slots, delegate (LevelSlot slot) { return slot.Id; },
                delegate (LevelSlot slot) { return new SpellSectionViewModel(slot.Id, slot.Title); },
                delegate (SpellSectionViewModel section, LevelSlot slot) { Fill(section, slot, offer, picks); });

            string missing = offer.FirstMissing(l.Choice);
            CanApply = !l.Applying && missing.Length == 0;
            ApplyLabel = l.Applying ? "LEVELLING…" : status != null ? "GAIN LEVEL " + status.TargetLevel : "LEVEL UP";
            Status = l.Applying ? string.Empty : missing.Length > 0 ? DisplayText.Plain("Still to choose: " + missing) : "Ready.";
        }

        private void Fill(SpellSectionViewModel section, LevelSlot slot, LevelUpOffer offer, Dictionary<string, List<string>> picks)
        {
            List<string> picked;
            if (!picks.TryGetValue(slot.Id, out picked)) { picked = new List<string>(); }
            var candidates = offer.OptionsOf(slot);
            int abilities = picked.FindAll(slot.IsAbility).Count;
            bool several = !slot.IsAsi && slot.Picks > 1;
            section.CountText = LevelChoices.Summary(slot, picked, offer.Options);
            section.Complete = LevelUpOffer.IsComplete(slot, picked) && picked.Count > 0;
            section.Over = false;
            section.Hint = slot.IsAsi ? LevelChoicesStepViewModel.AsiHint
                : several ? LevelChoicesStepViewModel.PicksHint(slot.Picks) + (slot.Required ? string.Empty : " Optional.")
                : slot.Required ? string.Empty : "Optional.";
            section.Options = ItemList.Sync(section.Options, candidates, delegate (BuilderOption o) { return "slot-" + slot.Id + "-" + o.Id; },
                delegate (BuilderOption o) { return new OptionViewModel("slot-" + slot.Id + "-" + o.Id, o, delegate (OptionViewModel vm) { _c.LevelUpPick(slot, vm.Option.Id); }); },
                delegate (OptionViewModel vm, BuilderOption o)
                {
                    bool selected = picked.Exists(delegate (string p) { return string.Equals(p, o.Id, StringComparison.OrdinalIgnoreCase); });
                    bool full = !selected && (slot.IsAsi ? slot.IsAbility(o.Id) && abilities >= 2 : several && picked.Count >= slot.Picks);
                    vm.Update(o, selected, false, full, slot.IsAsi ? LevelChoicesStepViewModel.FullHint : LevelChoicesStepViewModel.PicksFullHint(slot.Picks));
                });
        }
    }
}
