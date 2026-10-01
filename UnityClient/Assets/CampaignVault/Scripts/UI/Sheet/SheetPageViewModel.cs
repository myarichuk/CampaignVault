using System;
using System.Collections;
using Unity.Properties;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Sheet
{
    /// <summary>
    /// The character page (Templates/Sheet/SheetPage.uxml): any party id. The played character, and any player
    /// character, gets the full sheet; companions and NPCs get the parchment stat block. Raw engine values stay in
    /// the F12 inspector.
    /// </summary>
    public sealed class SheetPageViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly string _id;
        private CharacterSheet _sheet;
        private bool _loaded;
        private string _error = string.Empty;

        private string _notice = string.Empty;
        private string _noticeIcon = "character";
        private ViewModel _content;
        private bool _isYours;
        private bool _canPlayAs;

        public SheetPageViewModel(VaultAppState state, VaultController controller, string id)
        {
            _s = state;
            _c = controller;
            _id = id ?? string.Empty;
            PlayAs = delegate { _c.SetPcId(_id); };
            Watch(state, StateArea.Pc);
        }

        [CreateProperty] public string Notice { get { return _notice; } private set { Set(ref _notice, value); } }
        [CreateProperty] public string NoticeIcon { get { return _noticeIcon; } private set { Set(ref _noticeIcon, value); } }
        /// <summary>A <see cref="SheetViewModel"/> or <see cref="StatBlockViewModel"/>; null until loaded.</summary>
        [CreateProperty] public ViewModel Content { get { return _content; } private set { Set(ref _content, value); } }
        [CreateProperty] public bool IsYours { get { return _isYours; } private set { Set(ref _isYours, value); } }
        [CreateProperty] public bool CanPlayAs { get { return _canPlayAs; } private set { Set(ref _canPlayAs, value); } }
        [CreateProperty] public Action PlayAs { get; private set; }

        public bool Loaded { get { return _loaded; } }
        public bool IsStatBlock { get { return _content is StatBlockViewModel; } }

        /// <summary>The modal's title: the stat block carries the name itself, so its dialog is titled by role.</summary>
        public string Title
        {
            get
            {
                if (_sheet == null) { return "Character"; }
                return IsStatBlock ? (_sheet.IsCompanion ? "Companion" : "Character") : _sheet.Name;
            }
        }

        public IEnumerator Load()
        {
            McpOutcome<JsonValue> result = null;
            yield return _c.GetCharacterDetail(_id, delegate (McpOutcome<JsonValue> o) { result = o; });
            Show(result);
        }

        /// <summary>A get_entity reply (tests feed one directly).</summary>
        public void Show(McpOutcome<JsonValue> result)
        {
            _loaded = true;
            if (result != null && result.Ok) { _sheet = CharacterSheet.FromPayload(result.Data); }
            else { _error = result != null ? result.ErrorMessage : "no response"; }
            Refresh();
        }

        public override void Refresh()
        {
            if (_sheet == null)
            {
                Notice = _loaded ? DisplayText.Plain("Could not load " + _id + ": " + _error) : "Reading the sheet…";
                NoticeIcon = _loaded ? "warning" : "character";
                Content = null;
                IsYours = false;
                CanPlayAs = false;
                return;
            }
            Notice = string.Empty;
            bool yours = _id == _s.PcId;
            bool statBlock = !_sheet.IsPc && !yours;
            var current = _content as SheetViewModel;
            bool same = statBlock ? _content is StatBlockViewModel : current != null && current.Sheet == _sheet;
            if (!same) { Content = SheetViewModels.For(_sheet, statBlock); }
            IsYours = yours;
            CanPlayAs = !yours && _sheet.Id.Length > 0;
        }
    }
}
