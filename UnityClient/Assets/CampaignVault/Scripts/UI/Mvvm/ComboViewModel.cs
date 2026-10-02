using System;
using System.Collections.Generic;
using Unity.Properties;

namespace CampaignVault.UnityClient.UI.Mvvm
{
    /// <summary>
    /// A searchable pick-one list (a creature type, a creature to start from): a button showing the current pick that
    /// opens a search box over the options, each a button ("&lt;name&gt;-&lt;option&gt;"); picking one closes it. It opens
    /// in place rather than as a floating popup, so a scroll view never clips it. Shown with Templates/Common/Combo.uxml
    /// (through a ContentPresenter).
    /// </summary>
    public sealed class ComboViewModel : ViewModel, ITemplated
    {
        /// <summary>The most options shown at once; past that, typing narrows them.</summary>
        public const int MaxShown = 12;

        private readonly Action<string> _pick;
        private readonly string _placeholder;
        private List<KeyValuePair<string, string>> _options = new List<KeyValuePair<string, string>>();
        private string _selected = string.Empty;
        private string _label;
        private bool _open;
        private string _query = string.Empty;
        private string _more = string.Empty;
        private int _focusRequest;
        private List<ChoiceViewModel> _matches = new List<ChoiceViewModel>();

        public ComboViewModel(string name, string placeholder, Action<string> pick)
        {
            Name = name;
            _placeholder = placeholder ?? string.Empty;
            _label = _placeholder;
            _pick = pick;
            Toggle = delegate { SetOpen(!_open); };
        }

        public string Template { get { return "Common/Combo"; } }
        /// <summary>The option buttons are "&lt;name&gt;-&lt;option&gt;"; the toggle "&lt;name&gt;-open", the search box "&lt;name&gt;-search".</summary>
        public string Name { get; private set; }
        [CreateProperty] public string ToggleName { get { return Name + "-open"; } }
        [CreateProperty] public string SearchName { get { return Name + "-search"; } }
        [CreateProperty] public Action Toggle { get; private set; }
        /// <summary>The current pick's label, or the placeholder.</summary>
        [CreateProperty] public string Label { get { return _label; } private set { Set(ref _label, value); } }
        [CreateProperty] public bool HasValue { get { return _selected.Length > 0; } }
        [CreateProperty] public bool Open { get { return _open; } private set { Set(ref _open, value); } }
        [CreateProperty] public List<ChoiceViewModel> Matches { get { return _matches; } private set { SetList(ref _matches, value); } }
        /// <summary>"8 more: keep typing" when the search matches more than are shown.</summary>
        [CreateProperty] public string MoreText { get { return _more; } private set { Set(ref _more, value); } }
        /// <summary>Goes up each time the list opens, so the search box takes focus.</summary>
        [CreateProperty] public int FocusRequest { get { return _focusRequest; } private set { Set(ref _focusRequest, value); } }

        [CreateProperty]
        public string Query
        {
            get { return _query; }
            set { if (Set(ref _query, value ?? string.Empty)) { Filter(); } }
        }

        public string Selected { get { return _selected; } }

        /// <summary>The options (key, label) and the current pick (a key, or empty).</summary>
        public void SetOptions(IList<KeyValuePair<string, string>> options, string selected)
        {
            _options = new List<KeyValuePair<string, string>>(options);
            var current = _options.Find(delegate (KeyValuePair<string, string> o) { return string.Equals(o.Key, selected, StringComparison.OrdinalIgnoreCase); });
            string was = _selected;
            _selected = current.Key ?? (selected ?? string.Empty);
            Label = _selected.Length == 0 ? _placeholder : current.Value ?? _selected;
            if (was != _selected) { Notify("HasValue"); }
            Filter();
        }

        public void SetOpen(bool open)
        {
            if (open == _open) { return; }
            Open = open;
            if (open) { FocusRequest = _focusRequest + 1; }
            else { Query = string.Empty; }
        }

        private void Filter()
        {
            var shown = new List<KeyValuePair<string, string>>();
            int matched = 0;
            foreach (var o in _options)
            {
                if (_query.Length > 0 && o.Value.IndexOf(_query, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                matched++;
                if (shown.Count < MaxShown) { shown.Add(o); }
            }
            MoreText = matched > shown.Count ? (matched - shown.Count) + " more: keep typing" : matched == 0 ? "Nothing matches." : string.Empty;
            Matches = ItemList.Sync(_matches, shown, delegate (KeyValuePair<string, string> o) { return o.Key; },
                delegate (KeyValuePair<string, string> o)
                {
                    string key = o.Key;
                    return new ChoiceViewModel(key, o.Value, null, true, delegate { SetOpen(false); _pick(key); }, Name + "-" + Slug(key));
                },
                delegate (ChoiceViewModel vm, KeyValuePair<string, string> o) { vm.Update(o.Value, null, string.Equals(o.Key, _selected, StringComparison.OrdinalIgnoreCase)); });
        }

        public static string Slug(string key)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (key ?? string.Empty).ToLowerInvariant()) { sb.Append(char.IsLetterOrDigit(c) ? c : '-'); }
            return sb.ToString().Trim('-');
        }
    }
}
